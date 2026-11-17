using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Counts the active direct subscriptions of a checkout country
/// </summary>
public interface IDirectSubscriptionCounter
{
    Task<int> CountActiveDirect(string country);
}

/// <summary>
/// Database backed counter; uses its own scope because the cap is a singleton.
/// </summary>
public class DbDirectSubscriptionCounter(IServiceScopeFactory scopes) : IDirectSubscriptionCounter
{
    public async Task<int> CountActiveDirect(string country)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        return await UsDirectSubscriptionCap.ActiveDirect(db.Subscriptions, country, DateTime.UtcNow).CountAsync();
    }
}

/// <summary>
/// Owner decision: US buyers get direct Stripe/PayPal (0% tax collected by Coflnet) only while fewer than
/// SUBSCRIPTIONS:US_DIRECT_LIMIT (default 10) direct US subscriptions are active; from then on new US buyers are routed to
/// Stripe Managed Payments (Stripe collects the US sales tax). Existing subscriptions are untouched.
/// The count is cached for <see cref="CacheSeconds"/> seconds. A subscription counts once Stripe/PayPal reported it
/// (checkout completed / activated), not while its checkout is open, so concurrent checkouts around the limit can overshoot it
/// by a few; that is accepted.
/// </summary>
public class UsDirectSubscriptionCap
{
    public const int DefaultLimit = 10;
    public const string Country = "US";
    public const int CacheSeconds = 30;

    private readonly IDirectSubscriptionCounter counter;
    private readonly ILogger<UsDirectSubscriptionCap> logger;
    private readonly Func<DateTime> clock;
    private readonly object gate = new();
    private Task refresh = Task.CompletedTask;
    private DateTime refreshedAt = DateTime.MinValue;
    private volatile int state; // 0 = no successful count yet, 1 = below the limit, 2 = reached
    private int unknownLogged;
    private bool reachedLogged;

    public int Limit { get; }

    /// <summary>
    /// True when the last known count is at or above the limit. Until the first successful count it is true as well
    /// (conservative: US buyers go to Managed Payments rather than being sold to directly without a known count).
    /// </summary>
    public bool Reached
    {
        get
        {
            var current = state;
            if (current != 0)
                return current == 2;
            LogUnknownOnce();
            return true;
        }
    }

    private void LogUnknownOnce()
    {
        if (Interlocked.Exchange(ref unknownLogged, 1) == 1)
            return;
        logger?.LogWarning("US direct subscription count is not known yet: treating the cap as reached, new US buyers are routed to Stripe Managed Payments");
    }

    public UsDirectSubscriptionCap(IConfiguration config, IDirectSubscriptionCounter counter,
        ILogger<UsDirectSubscriptionCap> logger = null, Func<DateTime> clock = null)
    {
        Limit = int.TryParse(config?["SUBSCRIPTIONS:US_DIRECT_LIMIT"], out var limit) && limit >= 0 ? limit : DefaultLimit;
        this.counter = counter;
        this.logger = logger;
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// A direct subscription is active until it ended: every status except "expired", and "cancelled" ones only while
    /// their paid period (EndsAt) has not run out (cancelled without EndsAt counts as ended). Direct = Stripe without
    /// Managed Payments, or PayPal; the country is the checkout (VAT) country. Uses the (CheckoutCountry, Status) index.
    /// </summary>
    public static IQueryable<UserSubscription> ActiveDirect(IQueryable<UserSubscription> subscriptions, string country, DateTime now) =>
        subscriptions.Where(s => s.CheckoutCountry == country && !s.ManagedPayments
            && (s.ProviderSlug == SubscriptionProviders.Stripe || s.ProviderSlug == SubscriptionProviders.PayPal)
            && s.Status != "expired" && !(s.Status == "cancelled" && (s.EndsAt == null || s.EndsAt <= now)));

    /// <summary>
    /// Updates <see cref="Reached"/> when the cached count is older than <see cref="CacheSeconds"/>. A failing count keeps the
    /// previous value (before the first success: cap reached). Callers arriving while a count runs wait for it instead of routing with the old value, so the requests
    /// right after a restart wait for the first count.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (gate)
        {
            if (refresh.IsCompleted && (clock() - refreshedAt).TotalSeconds >= CacheSeconds)
            {
                refreshedAt = clock();
                refresh = CountAndApply();
            }
            return refresh;
        }
    }

    /// <summary>Never faults: a failing count is logged and keeps the previous routing</summary>
    private async Task CountAndApply()
    {
        await Task.Yield(); // count outside the lock
        try
        {
            Apply(await counter.CountActiveDirect(Country));
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Could not count the active direct US subscriptions; keeping the previous routing");
        }
    }

    private void Apply(int count)
    {
        var reached = count >= Limit;
        state = reached ? 2 : 1;
        if (!reached)
        {
            reachedLogged = false;
            return;
        }
        if (reachedLogged)
            return;
        reachedLogged = true;
        logger?.LogInformation("US direct subscription cap reached ({Count} active, limit {Limit}): new US buyers are routed to Stripe Managed Payments",
            count, Limit);
    }
}
