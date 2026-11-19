using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

/// <summary>
/// Owner decision (§ 309 Nr. 9 b BGB): a 52 week Stripe subscription continues as the 4-week plan of its tier after the
/// first year, moved by a subscription schedule; the first 4-week invoice converts it and grants 4 weeks.
/// </summary>
public class StripeRenewalTests : DirectSubscriptionTestBase
{
    private const long Year = 31536000;
    private const string Refund = nameof(PaymentRequest.Status.REFUND_PENDING);
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private Dictionary<string, string> checkoutMetadata;
    private TopUpProduct premiumYear;
    private TopUpProduct premiumPlusYear;
    private readonly List<StripeRenewalRequest> schedules = [];
    private readonly List<string> calls = [];
    /// <summary>The marker of each schedule the fake Stripe made, as its GetSchedule reports it</summary>
    private readonly Dictionary<string, string> markers = [];

    [SetUp]
    public async Task SetUpYearPlans()
    {
        premiumYear = await YearPlan("l_premium-year", 99.69m, "premium");
        premiumPlusYear = await YearPlan("l_prem_plus-year", 369.69m, "premium_plus", "premium");
        periodEnd = DateTime.UtcNow.AddDays(364);
        checkoutMetadata = new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_premium-year", [Keys.PriceCents] = "11863", [Keys.Mode] = "direct",
            [Keys.Country] = "DE", [Keys.RenewalProductSlug] = "l_premium", [Keys.RenewalPriceCents] = "1153"
        };
        remote = Snapshot();
        schedules.Clear();
        calls.Clear();
        markers.Clear();
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        stripe.Setup(s => s.EnsureVatTaxRate("DE", 1900)).ReturnsAsync("txr_de");
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeRenewalRequest request) =>
            {
                schedules.Add(request);
                calls.Add("schedule");
                remote = remote with { ScheduleId = "sub_sched_" + schedules.Count };
                markers[remote.ScheduleId] = StripeRenewalSchedule.Marker(request);
                return remote.ScheduleId;
            });
        // Our schedules as Stripe reports them (tests set up other schedules themselves)
        stripe.Setup(s => s.GetSchedule(It.IsAny<string>())).ReturnsAsync((string id) =>
            markers.TryGetValue(id, out var marker) ? new StripeScheduleInfo(id, false, true, marker) : null);
        stripe.Setup(s => s.ReleaseSchedule(It.IsAny<string>(), "sub_1")).ReturnsAsync((string id, string _) =>
        {
            calls.Add("release " + id);
            return remote = remote with { ScheduleId = null };
        });
    }

    private async Task<TopUpProduct> YearPlan(string slug, decimal price, params string[] tiers)
    {
        var plan = new TopUpProduct
        {
            Slug = slug, Title = slug, Price = price, Cost = 23400, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
            OwnershipSeconds = Year, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(plan);
        await db.SaveChangesAsync();
        foreach (var tier in tiers)
            await groups.AddProductToGroup(plan, tier);
        return plan;
    }

    /// <param name="plan">null: the checkout's inline product (plan from metadata); otherwise the plan product Stripe bills</param>
    private StripeSubscriptionSnapshot Snapshot(string plan = null, long priceCents = 11863, string scheduleId = null,
        bool cancelAtPeriodEnd = false, bool managed = false) =>
        new("sub_1", "active", cancelAtPeriodEnd, cancelAtPeriodEnd ? periodEnd : null, periodEnd, null, "cus_1",
            new Dictionary<string, string>(checkoutMetadata), "si_1", "price_1",
            plan == null ? "prod_checkout" : StripePlans.ProductId(plan), priceCents, "eur", managed ? "exclusive" : null, managed,
            DateTime.UtcNow, false, scheduleId);

    private static StripeInvoiceInfo Invoice(string id = "in_1", string reason = "subscription_create", long total = 11863,
        string currency = "eur") =>
        new(id, "sub_1", reason, "paid", total, total, currency, DateTime.UtcNow, "DE", "10115", "buyer@example.test",
            "Buyer", new Dictionary<string, string>());

    private Task<UserSubscription> Loaded() =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == "sub_1");

    [Test]
    public async Task PaidYearIsGrantedAndScheduledToContinueEveryFourWeeks()
    {
        var first = await Service(stripe.Object).StripeInvoicePaid(Invoice());
        Assert.That(first.IsRenewal, Is.False);

        var subscription = await Loaded();
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium-year"));
        Assert.That((subscription.RenewalProduct.Slug, subscription.RenewalPriceCents), Is.EqualTo(("l_premium", 1153L)));
        Assert.That(subscription.RenewalScheduleId, Is.EqualTo("sub_sched_1"));
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(Year)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(schedules.Single(), Is.EqualTo(new StripeRenewalRequest(1153, "eur", null, "l_premium", "4 weeks premium",
            periodEnd, "txr_de")), "gross 9.69 + 19% VAT, from the end of the paid year, with the receipt's VAT rate");
    }

    /// <summary>
    /// Stripe does not document the billing reason of the invoice after the schedule's phase change, so both are accepted
    /// </summary>
    [TestCase("subscription_cycle")]
    [TestCase("subscription_update")]
    public async Task FirstFourWeekInvoiceConvertsTheSubscriptionAndGrantsFourWeeks(string reason)
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "l_premium", priceCents: 1153, scheduleId: "sub_sched_1");

        var outcome = await service.StripeInvoicePaid(Invoice("in_2", reason, 1153));

        Assert.That(outcome, Is.Not.Null);
        Assert.That(outcome.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(outcome.EurAmount, Is.EqualTo(9.69m), "the net part of 11.53");
        Assert.That(outcome.IsProration, Is.False, "it pays 4 weeks (coins and creator share as a period payment)");
        Assert.That(outcome.IsRenewal, Is.True);
        var subscription = await Loaded();
        Assert.That((subscription.Product.Slug, subscription.PriceCents), Is.EqualTo(("l_premium", 1153L)));
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
        var access = await Access("sub_1");
        Assert.That(access.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(access.Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(Year + FourWeeks)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(await RefundMark("stripe", "in_2"), Is.Null);
        Assert.That(schedules, Has.Count.EqualTo(1), "a converted subscription is not scheduled again");

        Assert.That(await service.StripeInvoicePaid(Invoice("in_3", "subscription_cycle", 1153)), Is.Not.Null);
        Assert.That(await Grants("stripe-invoice-in_3"), Is.EqualTo(1));
    }

    [Test]
    public async Task FourWeekInvoiceAtAnotherPriceIsMarkedForRefund()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "l_premium", priceCents: 11863, scheduleId: "sub_sched_1");

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 11863)), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_2"), Is.Zero);
        Assert.That((await RefundMark("stripe", "in_2")).ToString(), Is.EqualTo(Refund));
    }

    /// <summary>
    /// The year a checkout sells is not scheduled before it is paid (a rejected first invoice cancels the subscription)
    /// </summary>
    [Test]
    public async Task CheckoutCompletedBeforeThePaymentDoesNotScheduleYet()
    {
        var service = Service(stripe.Object);
        await service.StripeCheckoutCompleted(new Stripe.Checkout.Session
        {
            Id = "cs_1", SubscriptionId = "sub_1", ClientReferenceId = "buyer", PaymentStatus = "unpaid",
            Metadata = new Dictionary<string, string>(checkoutMetadata)
        });
        Assert.That(schedules, Is.Empty);

        await service.StripeInvoicePaid(Invoice());
        Assert.That(schedules, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A failing schedule never blocks the grant; the next subscription event (or the hourly conversion) schedules it
    /// </summary>
    [Test]
    public async Task ScheduleFailureDoesNotBlockTheGrantAndIsRetried()
    {
        var service = Service(stripe.Object);
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ThrowsAsync(new Stripe.StripeException("down"));
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That((await Loaded()).RenewalScheduleId, Is.Null);

        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>())).ReturnsAsync("sub_sched_9");
        Assert.That(await service.ConvertDueRenewals(), Is.EqualTo(1));
        Assert.That((await Loaded()).RenewalScheduleId, Is.EqualTo("sub_sched_9"));
    }

    /// <summary>
    /// A schedule released elsewhere (dashboard, a resume in the Link app) would let the year renew: the subscription event
    /// schedules it again. Our own schedule is left alone.
    /// </summary>
    [Test]
    public async Task SubscriptionEventWithoutOurScheduleSchedulesAgain()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        await service.StripeSubscriptionChanged("sub_1");
        Assert.That(schedules, Has.Count.EqualTo(1), "still ours");

        remote = remote with { ScheduleId = null };
        await service.StripeSubscriptionChanged("sub_1");
        Assert.That(schedules, Has.Count.EqualTo(2));
        Assert.That((await Loaded()).RenewalScheduleId, Is.EqualTo("sub_sched_2"));
    }

    /// <summary>
    /// Subscriptions sold before the 4-week renewal (no renewal metadata) are converted by the hourly run: the 4-week plan of
    /// their tier with the creator code and the 4-week amount of their "forever" code, plus the VAT of their checkout country.
    /// </summary>
    [Test]
    public async Task ExistingYearIsConvertedWithItsCodesByTheHourlyRun()
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 10, IsActive = true });
        db.DiscountCodes.Add(new DiscountCode { Code = "BF2026", AmountType = "percent", Amount = 10, Duration = DiscountCode.EveryRenewal,
            PeriodAmounts = new() { [4] = 25, [52] = 50 } });
        db.Subscriptions.Add(new UserSubscription
        {
            User = await users.GetOrCreate("buyer"), Product = premiumYear, ProviderSlug = "stripe", ExternalId = "sub_1", Status = "active",
            CheckoutCountry = "DE", CreatorCode = "CREATOR", DiscountCode = "BF2026", PriceCents = 5338, Currency = "EUR",
            RenewsAt = periodEnd, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var service = Service(stripe.Object);
        await service.GrantPaidPeriod("buyer", premiumYear, "sub_1", "stripe-invoice-in_0");
        checkoutMetadata.Remove(Keys.RenewalProductSlug);
        checkoutMetadata.Remove(Keys.RenewalPriceCents);
        remote = Snapshot(priceCents: 5338);

        Assert.That(await service.ConvertDueRenewals(), Is.EqualTo(1));
        Assert.That(await service.ConvertDueRenewals(), Is.Zero, "scheduled, renewal far away: not even fetched again");

        var subscription = await Loaded();
        // 9.69 - 10% creator = 8.72, -25% (the 4-week amount) = 6.54, + 19% VAT = 7.7826
        Assert.That((subscription.RenewalProduct.Slug, subscription.RenewalPriceCents), Is.EqualTo(("l_premium", 778L)));
        Assert.That(schedules.Single().UnitAmount, Is.EqualTo(778));
        stripe.Verify(s => s.GetSubscription("sub_1"), Times.Once);
    }

    [Test]
    public async Task HourlyRunLeavesFourWeekCancelledAndConvertedSubscriptionsAlone()
    {
        var user = await users.GetOrCreate("buyer");
        db.Subscriptions.AddRange(
            new UserSubscription { User = user, Product = premium, ProviderSlug = "stripe", ExternalId = "sub_4w", Status = "active" },
            new UserSubscription { User = user, Product = premiumYear, ProviderSlug = "stripe", ExternalId = "sub_cancelled", Status = "cancelled" },
            new UserSubscription { User = user, Product = premiumYear, ProviderSlug = "lemonsqueezy", ExternalId = "ls_1", Status = "active" });
        await db.SaveChangesAsync();

        Assert.That(await Service(stripe.Object).ConvertDueRenewals(), Is.Zero);
        stripe.Verify(s => s.GetSubscription(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Stripe refuses cancel_at_period_end on a subscription a schedule manages: the renewal schedule is released first.
    /// Resuming schedules the renewal again.
    /// </summary>
    [Test]
    public async Task CancelReleasesTheScheduleAndResumeSchedulesItAgain()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() =>
        {
            calls.Add("cancel");
            return remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd };
        });
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = false, CancelAt = null });

        await service.CancelSubscription("buyer", "sub_1");
        Assert.That(calls, Is.EqualTo(new[] { "schedule", "release sub_sched_1", "cancel" }));
        Assert.That((await Loaded()).Status, Is.EqualTo("cancelled"));

        Assert.That(await service.ResumeSubscription("buyer", "sub_1"), Is.True);
        var subscription = await Loaded();
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That(subscription.RenewalScheduleId, Is.EqualTo("sub_sched_2"));
    }

    /// <summary>
    /// A resumed year that can not be scheduled would renew for another year: the resume is undone instead
    /// </summary>
    [Test]
    public async Task ResumeIsUndoneWhenTheRenewalCanNotBeScheduled()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd });
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = false, CancelAt = null });
        await service.CancelSubscription("buyer", "sub_1");
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ThrowsAsync(new Stripe.StripeException("down"));

        Assert.ThrowsAsync<ApiException>(() => service.ResumeSubscription("buyer", "sub_1"));

        stripe.Verify(s => s.SetCancelAtPeriodEnd("sub_1", true), Times.Exactly(2));
        Assert.That(remote.CancelAtPeriodEnd, Is.True);
    }

    /// <summary>
    /// A tier switch during the year releases the schedule, swaps the price and schedules the 4-week plan of the new tier
    /// </summary>
    [Test]
    public async Task PlanSwitchDuringTheYearContinuesAsTheFourWeekPlanOfTheNewTier()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        var changes = new List<StripePriceChangeRequest>();
        stripe.Setup(s => s.ChangePrice(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripePriceChangeRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot current, StripePriceChangeRequest request) =>
            {
                Assert.That(current.ScheduleId, Is.Null, "released before the price is changed");
                changes.Add(request);
                remote = Snapshot(plan: request.PlanSlug, priceCents: request.UnitAmount);
                return new StripePriceChange(remote, "in_up", "paid");
            });

        Assert.That((await service.ChangePlan("buyer", "sub_1", "l_prem_plus-year")).Status, Is.EqualTo("completed"));

        Assert.That(changes.Single().UnitAmount, Is.EqualTo(43993), "369.69 + 19% VAT");
        Assert.That(calls, Is.EqualTo(new[] { "schedule", "release sub_sched_1", "schedule" }));
        // 35.69 + 19% VAT = 42.4711
        Assert.That((schedules.Last().PlanSlug, schedules.Last().UnitAmount), Is.EqualTo(("l_prem_plus", 4247L)));
        var subscription = await Loaded();
        Assert.That((subscription.RenewalProduct.Slug, subscription.RenewalPriceCents), Is.EqualTo(("l_prem_plus", 4247L)));
    }

    /// <summary>
    /// The schedule was missing and Stripe renewed for another year: the buyer paid and gets the year (logged as an error to
    /// settle by hand), and the subscription is scheduled again for its next renewal.
    /// </summary>
    [Test]
    public async Task YearRenewedWithoutTheScheduleIsGrantedAndScheduledAgain()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = remote with { ScheduleId = null };

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle")), Is.Not.Null);

        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium-year"));
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
        Assert.That(schedules, Has.Count.EqualTo(2));
    }

    /// <summary>A schedule a failed attempt left attached (only the current phase) whose id was never stored</summary>
    private void ScheduleFailsLeavingAHalfSchedule()
    {
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .Callback(() => remote = remote with { ScheduleId = "sub_sched_half" })
            .ThrowsAsync(new Stripe.StripeException("update failed"));
    }

    /// <summary>
    /// Regression: a schedule whose id was never stored (the attempt failed after from_subscription) was not released, so
    /// Stripe refused the buyer's cancellation
    /// </summary>
    [Test]
    public async Task CancelReleasesAScheduleWithoutPlannedChangesWhoseIdWasNeverStored()
    {
        ScheduleFailsLeavingAHalfSchedule();
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.GetSchedule("sub_sched_half")).ReturnsAsync(new StripeScheduleInfo("sub_sched_half", false));
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() =>
        {
            calls.Add("cancel");
            return remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd };
        });

        await service.CancelSubscription("buyer", "sub_1");

        Assert.That(calls, Is.EqualTo(new[] { "release sub_sched_half", "cancel" }));
    }

    /// <summary>
    /// Regression: after the conversion (now a 4-week plan) our schedule whose id was never stored was not released, so
    /// Stripe refused the buyer's cancellation. Our schedules are recognised by their marker.
    /// </summary>
    [Test]
    public async Task CancelAfterTheConversionReleasesOurScheduleByItsMarker()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "l_premium", priceCents: 1153, scheduleId: "sub_sched_lost");
        await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 1153));
        await db.Subscriptions.Where(s => s.ExternalId == "sub_1")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RenewalScheduleId, (string)null));
        db.ChangeTracker.Clear();
        stripe.Setup(s => s.GetSchedule("sub_sched_lost")).ReturnsAsync(new StripeScheduleInfo("sub_sched_lost", true, true));
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() =>
        {
            calls.Add("cancel");
            return remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd };
        });
        calls.Clear();

        await service.CancelSubscription("buyer", "sub_1");

        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium"), "converted");
        Assert.That(calls, Is.EqualTo(new[] { "release sub_sched_lost", "cancel" }));
    }

    [Test]
    public async Task CancelLeavesAScheduleWithChangesPlannedBySomeoneElse()
    {
        ScheduleFailsLeavingAHalfSchedule();
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.GetSchedule("sub_sched_half")).ReturnsAsync(new StripeScheduleInfo("sub_sched_half", true));
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd });

        await service.CancelSubscription("buyer", "sub_1");

        stripe.Verify(s => s.ReleaseSchedule(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Regression: when scheduling a resumed year failed after attaching a schedule, the re-cancel was refused by Stripe and the
    /// subscription stayed resumed, renewing for another year. The schedule is released before cancelling again.
    /// </summary>
    [Test]
    public async Task UndoneResumeReleasesTheHalfMadeScheduleBeforeCancellingAgain()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() =>
        {
            calls.Add("cancel");
            return remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd };
        });
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = false, CancelAt = null });
        await service.CancelSubscription("buyer", "sub_1");
        ScheduleFailsLeavingAHalfSchedule();
        stripe.Setup(s => s.GetSchedule("sub_sched_half")).ReturnsAsync(new StripeScheduleInfo("sub_sched_half", false));
        calls.Clear();

        Assert.ThrowsAsync<ApiException>(() => service.ResumeSubscription("buyer", "sub_1"));

        Assert.That(calls, Is.EqualTo(new[] { "release sub_sched_half", "cancel" }));
        Assert.That(remote.CancelAtPeriodEnd, Is.True);
    }

    /// <summary>
    /// Regression: replicas running the conversion in step collided on the change lease and logged an error every hour;
    /// a subscription changed elsewhere is skipped quietly and checked by the next run
    /// </summary>
    [Test]
    public async Task ConversionSkipsASubscriptionBeingChangedElsewhereWithoutAnError()
    {
        var logger = new ListLogger();
        var service = Service(stripe.Object, logger: logger);
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ThrowsAsync(new Stripe.StripeException("down"));
        await service.StripeInvoicePaid(Invoice());
        logger.Entries.Clear();
        await db.Subscriptions.Where(s => s.ExternalId == "sub_1")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ChangeLockUntil, DateTime.UtcNow.AddMinutes(5)));

        Assert.That(await service.ConvertDueRenewals(), Is.Zero);

        Assert.That(logger.Entries.Where(e => e.Level >= LogLevel.Warning), Is.Empty);
        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("sub_1")), Is.True);
    }

    /// <summary>
    /// Regression: a 13/52 week subscription without renewal terms (no 4-week plan of its tier) that renewed for its full
    /// period logged nothing, because the check needed the renewal plan
    /// </summary>
    [Test]
    public async Task FullPeriodRenewalWithoutRenewalTermsIsLoggedAsAnError()
    {
        await YearPlan("l_bazaarpro-year", 99.69m);
        checkoutMetadata[Keys.ProductSlug] = "l_bazaarpro-year";
        checkoutMetadata.Remove(Keys.RenewalProductSlug);
        checkoutMetadata.Remove(Keys.RenewalPriceCents);
        remote = Snapshot();
        var logger = new ListLogger();
        var service = Service(stripe.Object, logger: logger);
        await service.StripeInvoicePaid(Invoice());
        Assert.That((await Loaded()).RenewalProduct, Is.Null);

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle")), Is.Not.Null);

        Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("in_2") && e.Message.Contains("refund the difference")), Is.True);
    }

    /// <summary>
    /// Regression: a redelivered first 4-week invoice (billing reason "updated") was taken for a proration once the
    /// subscription was converted, so retrying its recording would record it as one
    /// </summary>
    [Test]
    public async Task RedeliveredFirstFourWeekInvoiceIsStillAPeriodPayment()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "l_premium", priceCents: 1153, scheduleId: "sub_sched_1");
        await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 1153));

        var redelivered = await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 1153));

        Assert.That(redelivered.IsProration, Is.False);
        Assert.That(redelivered.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(redelivered.EurAmount, Is.EqualTo(9.69m));
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
    }

    private sealed class ListLogger : ILogger<SubscriptionService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Test]
    public async Task ManagedPaymentsYearContinuesAtTheNetPriceWithoutAVatRate()
    {
        checkoutMetadata[Keys.Mode] = "managed";
        checkoutMetadata[Keys.PriceCents] = "9969";
        checkoutMetadata[Keys.RenewalPriceCents] = "969";
        remote = Snapshot(priceCents: 9969, managed: true);

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 52000, currency: "brl")), Is.Not.Null);

        Assert.That(schedules.Single(), Is.EqualTo(new StripeRenewalRequest(969, "eur", "exclusive", "l_premium", "4 weeks premium", periodEnd)));
        stripe.Verify(s => s.EnsureVatTaxRate(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// With "renew for another term" off (the default), the hourly recheck shortly before the renewal leaves a schedule that plans
    /// exactly the renewal alone: no Stripe update, no conversion before the 4-week invoice
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task MatchingScheduleIsLeftAloneByTheHourlyRecheck(bool managed)
    {
        periodEnd = DateTime.UtcNow.AddDays(2);
        if (managed)
        {
            checkoutMetadata[Keys.Mode] = "managed";
            checkoutMetadata[Keys.PriceCents] = "9969";
            checkoutMetadata[Keys.RenewalPriceCents] = "969";
        }
        remote = Snapshot(priceCents: managed ? 9969 : 11863, managed: managed);
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);

        Assert.That(await service.ConvertDueRenewals(), Is.Zero);
        Assert.That(await service.ConvertDueRenewals(), Is.Zero);

        Assert.That(schedules, Has.Count.EqualTo(1), "only the schedule of the paid year");
        stripe.Verify(s => s.GetSchedule("sub_sched_1"), Times.Exactly(2));
        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium-year"));
    }

    [Test]
    public async Task FourWeekSubscriptionsAreNeverScheduled()
    {
        checkoutMetadata[Keys.ProductSlug] = "l_premium";
        checkoutMetadata[Keys.PriceCents] = "1153";
        remote = Snapshot(priceCents: 1153);
        var service = Service(stripe.Object);

        await service.StripeInvoicePaid(Invoice(total: 1153));
        await service.StripeSubscriptionChanged("sub_1");

        Assert.That((await Loaded()).RenewalProduct, Is.Null, "the renewal metadata of a 4-week plan is ignored");
        Assert.That(schedules, Is.Empty);
    }
}
