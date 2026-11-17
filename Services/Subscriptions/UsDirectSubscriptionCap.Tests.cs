using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Payments.Services.Subscriptions;

public class UsDirectSubscriptionCapTests : DirectSubscriptionTestBase
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class CountingCounter : IDirectSubscriptionCounter
    {
        public int Count { get; set; }
        public int Calls { get; private set; }
        public bool Fails { get; set; }

        public Task<int> CountActiveDirect(string country)
        {
            Calls++;
            return Fails ? throw new InvalidOperationException("db down") : Task.FromResult(Count);
        }
    }

    private sealed class MessageLogger : ILogger<UsDirectSubscriptionCap>
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel == LogLevel.Information)
                Infos.Add(formatter(state, exception));
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private UserSubscription Sub(string id, string status = "active", string provider = "stripe", string country = "US",
        bool managed = false, DateTime? endsAt = null) => new()
    {
        ExternalId = id, ProviderSlug = provider, CheckoutCountry = country, ManagedPayments = managed, Status = status, EndsAt = endsAt,
        User = users.GetOrCreate("buyer-" + id).Result, Product = premium
    };

    private async Task<List<string>> Active()
    {
        db.ChangeTracker.Clear();
        return await UsDirectSubscriptionCap.ActiveDirect(db.Subscriptions, "US", Now).Select(s => s.ExternalId).OrderBy(s => s).ToListAsync();
    }

    /// <summary>
    /// What counts: direct (Stripe without Managed Payments, PayPal) subscriptions of the US checkout country that did not end
    /// (not expired, not cancelled with a run out or missing EndsAt).
    /// </summary>
    [Test]
    public async Task OnlyActiveDirectUsSubscriptionsCount()
    {
        db.Subscriptions.AddRange(
            Sub("active"), Sub("paypal", provider: "paypal"), Sub("past_due", "past_due"), Sub("paused", "paused"),
            Sub("cancelled_running", "cancelled", endsAt: Now.AddDays(3)),
            Sub("cancelled_over", "cancelled", endsAt: Now.AddDays(-1)), Sub("cancelled_open", "cancelled"),
            Sub("expired", "expired"), Sub("managed", managed: true), Sub("lemon", provider: "lemonsqueezy"),
            Sub("german", country: "DE"), Sub("nocountry", country: null));
        await db.SaveChangesAsync();

        Assert.That(await Active(), Is.EqualTo(new[] { "active", "cancelled_running", "past_due", "paused", "paypal" }));
    }

    [Test]
    public async Task ReachedAtTheLimitCachedAndLoggedOnce()
    {
        var counter = new CountingCounter { Count = 9 };
        var logger = new MessageLogger();
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, logger, () => clock);

        await cap.RefreshAsync();
        Assert.That(cap.Reached, Is.False);
        counter.Count = 10;
        await cap.RefreshAsync();
        Assert.That((cap.Reached, counter.Calls), Is.EqualTo((false, 1)), "within the cache time the count is not read again");

        clock = clock.AddSeconds(UsDirectSubscriptionCap.CacheSeconds + 1);
        await cap.RefreshAsync();
        clock = clock.AddSeconds(UsDirectSubscriptionCap.CacheSeconds + 1);
        await cap.RefreshAsync();

        Assert.That(cap.Reached, Is.True);
        Assert.That(logger.Infos, Has.Count.EqualTo(1));
        Assert.That(cap.Limit, Is.EqualTo(10));
    }

    [Test]
    public async Task AFailingCountKeepsThePreviousRouting()
    {
        var counter = new CountingCounter { Count = 12 };
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, null, () => clock);
        await cap.RefreshAsync();
        counter.Fails = true;
        clock = clock.AddMinutes(1);

        await cap.RefreshAsync();

        Assert.That(cap.Reached, Is.True);
    }

    private sealed class PendingCounter : IDirectSubscriptionCounter
    {
        public TaskCompletionSource<int> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task<int> CountActiveDirect(string country)
        {
            Calls++;
            return Result.Task;
        }
    }

    /// <summary>
    /// Regression: a request arriving while the first count ran (right after a restart, while the first DbContext is built)
    /// returned at once and was routed with the initial "not reached", so US buyers got direct checkouts above the cap.
    /// </summary>
    [Test]
    public async Task ARefreshDuringARunningCountWaitsForIt()
    {
        var counter = new PendingCounter();
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, null, () => Now);

        var first = cap.RefreshAsync();
        var concurrent = cap.RefreshAsync();
        Assert.That(concurrent.IsCompleted, Is.False, "the concurrent request must not route before the count is known");

        counter.Result.SetResult(12);
        await Task.WhenAll(first, concurrent);

        Assert.That((cap.Reached, counter.Calls), Is.EqualTo((true, 1)));
    }

    /// <summary>
    /// Regression: before the first count the cap counted as not reached, so US buyers got direct checkouts while the
    /// count was unknown (restart, database down). Now it counts as reached, logged once.
    /// </summary>
    [Test]
    public async Task UnknownCountCountsAsReachedAndIsLoggedOnce()
    {
        var counter = new CountingCounter { Fails = true };
        var logger = new MessageLogger();
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, logger, () => clock);
        Assert.That(cap.Reached, Is.True, "before any count");

        await cap.RefreshAsync();
        clock = clock.AddMinutes(1);
        await cap.RefreshAsync();

        Assert.That(cap.Reached, Is.True, "failed counts keep the unknown state");
        Assert.That(logger.Warnings.Count(w => w.Contains("not known yet")), Is.EqualTo(1));
    }

    [Test]
    public async Task AFailingCountAfterASuccessKeepsTheNotReachedState()
    {
        var counter = new CountingCounter { Count = 2 };
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, null, () => clock);
        await cap.RefreshAsync();
        counter.Fails = true;
        clock = clock.AddMinutes(1);

        await cap.RefreshAsync();

        Assert.That(cap.Reached, Is.False);
    }

    private static SubscriptionRouting RoutingWith(UsDirectSubscriptionCap cap) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            [SubscriptionTax.RateKeyPrefix + "US"] = "0",
            ["SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED"] = "true",
            ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "true",
            ["SUBSCRIPTIONS:PAYPAL_ENABLED"] = "true"
        }).Build(), cap);

    /// <summary>
    /// Regression: the direct-country check and the mode read the cap separately, so a refresh in between could give a US
    /// buyer a direct checkout without the IP-country check. A snapshot decides everything with the state it was taken in.
    /// </summary>
    [Test]
    public async Task ASnapshotIsNotChangedByALaterRefreshOfTheCap()
    {
        var counter = new CountingCounter { Count = 0 };
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, null, () => clock);
        await cap.RefreshAsync();
        var routing = RoutingWith(cap);
        var snapshot = routing.Capture();

        counter.Count = 10;
        clock = clock.AddMinutes(1);
        await cap.RefreshAsync();

        Assert.That(snapshot.IsDirectCountry("US"), Is.True);
        Assert.That(snapshot.StripeMode("US"), Is.EqualTo(StripeCheckoutMode.Direct));
        Assert.That(snapshot.PayPalAvailable("US"), Is.True);
        Assert.That(routing.StripeMode("US"), Is.EqualTo(StripeCheckoutMode.Managed), "a new snapshot sees the new state");
        Assert.That(routing.Capture().IsDirectCountry("US"), Is.False);
    }

    [TestCase(true, StripeCheckoutMode.Managed, false)]
    [TestCase(false, StripeCheckoutMode.Direct, true)]
    public void ASnapshotWithAFixedCapStateRoutesConsistently(bool reached, StripeCheckoutMode mode, bool direct)
    {
        var snapshot = RoutingWith(null).Capture(reached);

        Assert.That((snapshot.StripeMode("US"), snapshot.IsDirectCountry("US"), snapshot.PayPalAvailable("US")), Is.EqualTo((mode, direct, direct)));
        Assert.That(snapshot.ProvidersFor("US").Select(p => p.Mode), Is.All.EqualTo(direct ? "direct" : "managed"));
    }
}
