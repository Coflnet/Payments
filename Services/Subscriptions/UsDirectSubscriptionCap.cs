using System;
using System.Collections.Generic;
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
/// Counts the active direct subscriptions of a checkout country per checkout region (state)
/// </summary>
public interface IDirectSubscriptionCounter
{
    /// <returns>Active direct subscriptions per region; subscriptions without a region are under <see cref="UsDirectSubscriptionCap.UnknownRegion"/></returns>
    Task<IReadOnlyDictionary<string, int>> CountActiveDirectByRegion(string country);
}

/// <summary>
/// Database backed counter; uses its own scope because the cap is a singleton.
/// </summary>
public class DbDirectSubscriptionCounter(IServiceScopeFactory scopes) : IDirectSubscriptionCounter
{
    public async Task<IReadOnlyDictionary<string, int>> CountActiveDirectByRegion(string country)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var rows = await UsDirectSubscriptionCap.ActiveDirect(db.Subscriptions, country, DateTime.UtcNow)
            .GroupBy(s => s.CheckoutRegion).Select(g => new { Region = g.Key, Count = g.Count() }).ToListAsync();
        return rows.ToDictionary(r => r.Region ?? UsDirectSubscriptionCap.UnknownRegion, r => r.Count);
    }
}

/// <summary>
/// Owner decision: US buyers get direct Stripe/PayPal (0% tax collected by Coflnet) only while fewer than
/// SUBSCRIPTIONS:US_DIRECT_LIMIT (default 10) direct subscriptions are active for the buyer's US STATE; from then on new buyers of that
/// state are routed to Stripe Managed Payments (Stripe collects the sales tax). Other states are not affected. Existing subscriptions are untouched.
/// The state is the checkout region the caller passes (Cloudflare cf-region-code); an unknown region counts as capped (Managed Payments).
/// Subscriptions without a region (none exist in production yet) are counted under <see cref="UnknownRegion"/>, a bucket that
/// belongs to no state and so affects none.
/// The counts are cached for <see cref="CacheSeconds"/> seconds (one grouped query for all states). A subscription counts once Stripe/PayPal
/// reported it (checkout completed / activated), not while its checkout is open, so concurrent checkouts around the limit can overshoot it
/// by a few; that is accepted.
/// </summary>
public class UsDirectSubscriptionCap
{
    public const int DefaultLimit = 10;
    public const string Country = "US";
    public const int CacheSeconds = 30;
    /// <summary>Bucket of subscriptions without a checkout region; never matches a state</summary>
    public const string UnknownRegion = "";

    private readonly IDirectSubscriptionCounter counter;
    private readonly ILogger<UsDirectSubscriptionCap> logger;
    private readonly Func<DateTime> clock;
    private readonly object gate = new();
    private Task refresh = Task.CompletedTask;
    private DateTime refreshedAt = DateTime.MinValue;
    private volatile IReadOnlyDictionary<string, int> counts; // null = no successful count yet
    private int unknownLogged;
    private readonly HashSet<string> reachedLogged = [];

    public int Limit { get; }

    /// <summary>
    /// True when the last known count of the state is at or above the limit. Until the first successful count, and for a missing
    /// or invalid region, it is true as well (conservative: such US buyers go to Managed Payments rather than being sold to directly).
    /// </summary>
    public bool ReachedFor(string region)
    {
        var current = counts;
        if (current == null)
        {
            LogUnknownOnce();
            return true;
        }
        var state = NormalizeRegion(region);
        return state == null || current.GetValueOrDefault(state) >= Limit;
    }

    /// <summary>
    /// ISO 3166-2 subdivision code without country prefix ("CA", "NY"): 1-3 letters or digits, upper case; null for anything else.
    /// </summary>
    public static string NormalizeRegion(string region)
    {
        var state = region?.Trim().ToUpperInvariant();
        return state is { Length: >= 1 and <= 3 } && state.All(char.IsAsciiLetterOrDigit) ? state : null;
    }

    private void LogUnknownOnce()
    {
        if (Interlocked.Exchange(ref unknownLogged, 1) == 1)
            return;
        logger?.LogWarning("US direct subscription counts are not known yet: treating every state's cap as reached, new US buyers are routed to Stripe Managed Payments");
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
    /// Managed Payments, or PayPal; the country is the checkout (VAT) country. Uses the (CheckoutCountry, CheckoutRegion, Status) index.
    /// </summary>
    public static IQueryable<UserSubscription> ActiveDirect(IQueryable<UserSubscription> subscriptions, string country, DateTime now) =>
        subscriptions.Where(s => s.CheckoutCountry == country && !s.ManagedPayments
            && (s.ProviderSlug == SubscriptionProviders.Stripe || s.ProviderSlug == SubscriptionProviders.PayPal)
            && s.Status != "expired" && !(s.Status == "cancelled" && (s.EndsAt == null || s.EndsAt <= now)));

    /// <summary>
    /// Updates the counts when the cached ones are older than <see cref="CacheSeconds"/>. A failing count keeps the
    /// previous value (before the first success: every state capped). Callers arriving while a count runs wait for it instead of routing with the old value, so the requests
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
            Apply(await counter.CountActiveDirectByRegion(Country));
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Could not count the active direct US subscriptions; keeping the previous routing");
        }
    }

    private void Apply(IReadOnlyDictionary<string, int> newCounts)
    {
        counts = newCounts;
        var reached = newCounts.Where(c => c.Key != UnknownRegion && c.Value >= Limit).ToList();
        reachedLogged.IntersectWith(reached.Select(c => c.Key));
        foreach (var (region, count) in reached)
        {
            if (reachedLogged.Add(region))
                logger?.LogInformation("US direct subscription cap reached in {Region} ({Count} active, limit {Limit}): new buyers of that state are routed to Stripe Managed Payments",
                    region, count, Limit);
        }
    }
}
