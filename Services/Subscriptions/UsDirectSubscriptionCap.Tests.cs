using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Payments.Services.Subscriptions;

public class UsDirectSubscriptionCapTests : DirectSubscriptionTestBase
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Reports <see cref="Count"/> active subscriptions for the state CA and none for the others</summary>
    private sealed class CountingCounter : IDirectSubscriptionCounter
    {
        public int Count { get; set; }
        public Dictionary<string, int> Others { get; } = [];
        public int Calls { get; private set; }
        public bool Fails { get; set; }

        public Task<IReadOnlyDictionary<string, int>> CountActiveDirectByRegion(string country)
        {
            Calls++;
            if (Fails)
                throw new InvalidOperationException("db down");
            var counts = new Dictionary<string, int>(Others) { ["CA"] = Count };
            return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
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
        bool managed = false, DateTime? endsAt = null, string region = "CA") => new()
    {
        ExternalId = id, ProviderSlug = provider, CheckoutCountry = country, CheckoutRegion = region, ManagedPayments = managed, Status = status, EndsAt = endsAt,
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
            Sub("german", country: "DE"), Sub("nocountry", country: null), Sub("noregion", region: null));
        await db.SaveChangesAsync();

        Assert.That(await Active(), Is.EqualTo(new[] { "active", "cancelled_running", "noregion", "past_due", "paused", "paypal" }));
    }

    /// <summary>
    /// The real counter groups by state in one query; subscriptions without a region are counted under the unknown bucket
    /// (which belongs to no state).
    /// </summary>
    [Test]
    public async Task TheDatabaseCounterGroupsActiveDirectSubscriptionsByState()
    {
        db.Subscriptions.AddRange(Sub("ca1"), Sub("ca2", provider: "paypal"), Sub("ny1", region: "NY"), Sub("none1", region: null),
            Sub("ca_expired", "expired"), Sub("ca_managed", managed: true), Sub("de", country: "DE", region: "BY"));
        await db.SaveChangesAsync();
        var scopes = new ServiceCollection().AddSingleton(db).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var counts = await new DbDirectSubscriptionCounter(scopes).CountActiveDirectByRegion("US");

        Assert.That(counts, Is.EquivalentTo(new Dictionary<string, int> { ["CA"] = 2, ["NY"] = 1, [UsDirectSubscriptionCap.UnknownRegion] = 1 }));
    }

    /// <summary>
    /// Owner decision: the cap applies per state: 10 in CA cap CA but not NY; an unknown or invalid state counts as capped;
    /// subscriptions without a state (the unknown bucket) cap no state.
    /// </summary>
    [Test]
    public async Task TheCapAppliesPerStateAndAnUnknownStateIsCapped()
    {
        var counter = new CountingCounter { Count = 10 };
        counter.Others["NY"] = 9;
        counter.Others[UsDirectSubscriptionCap.UnknownRegion] = 50;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, null, () => Now);
        await cap.RefreshAsync();

        Assert.That(cap.ReachedFor("CA"), Is.True);
        Assert.That(cap.ReachedFor(" ca "), Is.True, "normalized");
        Assert.That(cap.ReachedFor("NY"), Is.False);
        Assert.That(cap.ReachedFor("TX"), Is.False, "a state without subscriptions");
        Assert.That(cap.ReachedFor(null), Is.True, "unknown region");
        Assert.That(cap.ReachedFor(""), Is.True);
        Assert.That(cap.ReachedFor("California"), Is.True, "not a subdivision code");
        Assert.That(cap.ReachedFor(UsDirectSubscriptionCap.UnknownRegion), Is.True, "the unknown bucket is not a state");
    }

    /// <summary>
    /// Regression: TopUpOptions.Region had MaxLength(3), so an unexpected edge value ("US-CA", padded " CA ") failed the model
    /// validation of every checkout (one-time purchases included) with 400 instead of counting as an unknown state.
    /// </summary>
    [TestCase("US-CA")]
    [TestCase(" CA ")]
    [TestCase("Ontario")]
    public void AnUnexpectedRegionPassesValidationAndCountsAsUnknown(string region)
    {
        var options = new TopUpOptions { Region = region };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options), results, validateAllProperties: true);

        Assert.That(valid, Is.True, string.Join("; ", results.Select(r => r.ErrorMessage)));
        Assert.That(UsDirectSubscriptionCap.NormalizeRegion(region), region.Trim() == "CA" ? Is.EqualTo("CA") : Is.Null);
    }

    [Test]
    public async Task EachStateLogsItsCapOnce()
    {
        var counter = new CountingCounter { Count = 10 };
        counter.Others["NY"] = 10;
        var logger = new MessageLogger();
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, logger, () => clock);
        await cap.RefreshAsync();
        clock = clock.AddMinutes(1);
        await cap.RefreshAsync();

        Assert.That(logger.Infos, Has.Count.EqualTo(2));
        Assert.That(logger.Infos, Has.One.Contain("CA").And.One.Contain("NY"));
    }

    [Test]
    public async Task ReachedAtTheLimitCachedAndLoggedOnce()
    {
        var counter = new CountingCounter { Count = 9 };
        var logger = new MessageLogger();
        var clock = Now;
        var cap = new UsDirectSubscriptionCap(new ConfigurationBuilder().Build(), counter, logger, () => clock);

        await cap.RefreshAsync();
        Assert.That(cap.ReachedFor("CA"), Is.False);
        counter.Count = 10;
        await cap.RefreshAsync();
        Assert.That((cap.ReachedFor("CA"), counter.Calls), Is.EqualTo((false, 1)), "within the cache time the count is not read again");

        clock = clock.AddSeconds(UsDirectSubscriptionCap.CacheSeconds + 1);
        await cap.RefreshAsync();
        clock = clock.AddSeconds(UsDirectSubscriptionCap.CacheSeconds + 1);
        await cap.RefreshAsync();

        Assert.That(cap.ReachedFor("CA"), Is.True);
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

        Assert.That(cap.ReachedFor("CA"), Is.True);
    }

    private sealed class PendingCounter : IDirectSubscriptionCounter
    {
        public TaskCompletionSource<IReadOnlyDictionary<string, int>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<string, int>> CountActiveDirectByRegion(string country)
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

        counter.Result.SetResult(new Dictionary<string, int> { ["CA"] = 12 });
        await Task.WhenAll(first, concurrent);

        Assert.That((cap.ReachedFor("CA"), counter.Calls), Is.EqualTo((true, 1)));
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
        Assert.That(cap.ReachedFor("CA"), Is.True, "before any count");

        await cap.RefreshAsync();
        clock = clock.AddMinutes(1);
        await cap.RefreshAsync();

        Assert.That(cap.ReachedFor("CA"), Is.True, "failed counts keep the unknown state");
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

        Assert.That(cap.ReachedFor("CA"), Is.False);
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
        var snapshot = routing.Capture("CA");

        counter.Count = 10;
        clock = clock.AddMinutes(1);
        await cap.RefreshAsync();

        Assert.That(snapshot.IsDirectCountry("US"), Is.True);
        Assert.That(snapshot.StripeMode("US"), Is.EqualTo(StripeCheckoutMode.Direct));
        Assert.That(snapshot.PayPalAvailable("US"), Is.True);
        Assert.That(routing.Capture("CA").StripeMode("US"), Is.EqualTo(StripeCheckoutMode.Managed), "a new snapshot sees the new state");
        Assert.That(routing.Capture("CA").IsDirectCountry("US"), Is.False);
    }

    [TestCase(true, StripeCheckoutMode.Managed, false)]
    [TestCase(false, StripeCheckoutMode.Direct, true)]
    public void ASnapshotWithAFixedCapStateRoutesConsistently(bool reached, StripeCheckoutMode mode, bool direct)
    {
        var snapshot = RoutingWith(null).Capture(usCapReached: reached);

        Assert.That((snapshot.StripeMode("US"), snapshot.IsDirectCountry("US"), snapshot.PayPalAvailable("US")), Is.EqualTo((mode, direct, direct)));
        Assert.That(snapshot.ProvidersFor("US").Select(p => p.Mode), Is.All.EqualTo(direct ? "direct" : "managed"));
    }
}
