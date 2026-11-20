using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

/// <summary>
/// Owner decision: a running 4-week (or 13 week) subscription switches to a longer term of its tier at once. The new term is charged
/// now (an active automatic promotion lowers that first charge), the paid days left are kept: the term is granted on top of the
/// current access end, and after it the subscription continues every 4 weeks.
/// </summary>
public class StripeTermSwitchTests : DirectSubscriptionTestBase
{
    private const long Year = 31536000;
    private const long Quarter = 7862400;
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private Dictionary<string, string> metadata;
    private readonly List<StripeRenewalRequest> schedules = [];
    private readonly List<StripeTermSwitchRequest> switches = [];
    private SubscriptionService service;

    [SetUp]
    public async Task SetUpPaidFourWeeks()
    {
        foreach (var (slug, title, price, seconds) in new[] { ("l_premium-quarter", "13 weeks premium", 26.69m, Quarter), ("l_premium-year", "52 weeks premium", 99.69m, Year) })
        {
            var plan = new TopUpProduct
            {
                Slug = slug, Title = title, Price = price, Cost = 6000, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
                OwnershipSeconds = seconds, Type = Product.ProductType.SERVICE
            };
            db.TopUpProducts.Add(plan);
            await db.SaveChangesAsync();
            await groups.AddProductToGroup(plan, "premium");
        }
        periodEnd = Seconds(DateTime.UtcNow.AddDays(20));
        metadata = new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_premium", [Keys.PriceCents] = "1153", [Keys.Mode] = "direct", [Keys.Country] = "DE"
        };
        remote = Snapshot("prod_checkout", 1153, periodEnd);
        schedules.Clear();
        switches.Clear();
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        stripe.Setup(s => s.EnsureVatTaxRate("DE", 1900)).ReturnsAsync("txr_de");
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeRenewalRequest request) =>
            {
                schedules.Add(request);
                remote = remote with { ScheduleId = "sub_sched_" + schedules.Count };
                return remote.ScheduleId;
            });
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeTermSwitchRequest request) =>
            {
                switches.Add(request);
                remote = Switched(request);
                return new StripeTermSwitch(remote, Invoice("in_switch", "subscription_update", request.UnitAmount), "https://invoice.stripe.test/in_switch");
            });
        service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice("in_1", "subscription_create", 1153)), Is.Not.Null);
    }

    private static DateTime Seconds(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private StripeSubscriptionSnapshot Snapshot(string product, long cents, DateTime end, bool pendingUpdate = false) =>
        new("sub_1", "active", false, null, end, null, "cus_1", new Dictionary<string, string>(metadata), "si_1", "price_1",
            product, cents, "eur", null, false, DateTime.UtcNow, pendingUpdate);

    private StripeSubscriptionSnapshot Switched(StripeTermSwitchRequest request) =>
        Snapshot(StripePlans.ProductId(request.PlanSlug), request.UnitAmount, Seconds(DateTime.UtcNow.AddDays(7 * request.Weeks)));

    private static StripeInvoiceInfo Invoice(string id, string reason, long total, string status = "paid") =>
        new(id, "sub_1", reason, status, total, status == "paid" ? total : 0, "eur", DateTime.UtcNow, "DE", "10115",
            "buyer@example.test", "Buyer", new Dictionary<string, string>());

    /// <summary>The regular prices are shown when no promotion runs: 26.69 / 99.69 + 19% VAT</summary>
    private static SubscriptionTermSwitchRequest Request(int weeks, decimal? expected = null) => new()
    {
        Weeks = weeks, Declaration = StripeExtensionTests.Declaration("Switch now"),
        ExpectedChargedNow = expected ?? (weeks == 13 ? 31.76m : 118.63m)
    };

    private void AddPromotion() => db.DiscountCodes.Add(new DiscountCode
    {
        Code = "GTA6", Name = "Blackweek sale", AmountType = "percent", Amount = 25, PeriodAmounts = new() { [4] = 25, [13] = 40, [52] = 50 },
        Duration = DiscountCode.FirstPaymentOnly, SubscriptionOnly = true, AutoApply = true, IsActive = true,
        StartsAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(3)
    });

    private Task<UserSubscription> Loaded() =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == "sub_1");

    [Test]
    public async Task OptionsOfferBothLongerTermsAtTheirRegularPriceWithoutPromotion()
    {
        var expiry = (await Access("sub_1")).Expires;

        var options = await service.GetTermOptions("buyer", "sub_1");

        Assert.That((options.UnavailableReason, options.CurrentWeeks, options.CurrentAccessEndsAt), Is.EqualTo(((string)null, (int?)4, (DateTime?)expiry)));
        Assert.That(options.Terms.Select(t => (t.Weeks, t.ProductSlug, t.RegularPrice, t.ChargedNow)),
            Is.EqualTo(new[] { (13, "l_premium-quarter", 31.76m, 31.76m), (52, "l_premium-year", 118.63m, 118.63m) }), "26.69 / 99.69 + 19% VAT");
        var year = options.Terms[1];
        Assert.That((year.Promotion, year.Currency, year.TaxTreatment), Is.EqualTo(((SubscriptionTermPromotion)null, "EUR", "included")));
        Assert.That((year.CurrentAccessEndsAt, year.NewAccessEndsAt), Is.EqualTo((expiry, expiry.AddSeconds(Year))), "the paid days left are kept");
        Assert.That(year.CarriedOverDays, Is.EqualTo(28));
        Assert.That((year.RenewsAsProductSlug, year.RenewalPrice, year.RenewalWeeks), Is.EqualTo(("l_premium", 11.53m, 4)));
        Assert.That(year.FirstRenewalAt, Is.EqualTo(DateTime.UtcNow.AddDays(364)).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task OptionsChargeThePromotionPriceForThisFirstCharge()
    {
        AddPromotion();
        await db.SaveChangesAsync();

        var options = await service.GetTermOptions("buyer", "sub_1");

        // 26.69 - 40% = 16.01 + 19% = 19.05; 99.69 - 50% = 49.84 (banker's rounding) + 19% = 59.31
        Assert.That(options.Terms.Select(t => (t.RegularPrice, t.ChargedNow, t.Promotion.Code, t.Promotion.Amount)),
            Is.EqualTo(new[] { (31.76m, 19.05m, "GTA6", 40m), (118.63m, 59.31m, "GTA6", 50m) }));
        Assert.That(options.Terms.Select(t => t.RenewalPrice), Is.All.EqualTo(11.53m), "the promotion does not touch the 4-week continuation");
    }

    /// <summary>The switch invoice is granted once as a year on top of the current access end and the year continues every 4 weeks</summary>
    [Test]
    public async Task SwitchGrantsTheYearOnTopAndSchedulesTheFourWeekContinuation()
    {
        AddPromotion();
        await db.SaveChangesAsync();
        var expiry = (await Access("sub_1")).Expires;

        var result = await service.SwitchTerm("buyer", "sub_1", Request(52, 59.31m));

        Assert.That((result.Switched, result.Status, result.ChargedNow, result.PromotionCode, result.ApprovalUrl),
            Is.EqualTo((true, "completed", 59.31m, "GTA6", (string)null)));
        Assert.That(result.AccessUntil, Is.EqualTo(expiry.AddSeconds(Year)));
        Assert.That(switches.Single(), Is.EqualTo(new StripeTermSwitchRequest(5931, "eur", 52, null, "l_premium-year", "52 weeks premium")));
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry.AddSeconds(Year)));
        Assert.That(await Grants("stripe-invoice-in_switch"), Is.EqualTo(1));
        var subscription = await Loaded();
        Assert.That((subscription.Product.Slug, subscription.PriceCents, subscription.RenewalProduct.Slug, subscription.RenewalPriceCents),
            Is.EqualTo(("l_premium-year", (long?)5931, "l_premium", (long?)1153)));
        Assert.That(schedules.Single(), Is.EqualTo(new StripeRenewalRequest(1153, "eur", null, "l_premium", "4 weeks premium",
            remote.CurrentPeriodEnd.Value, "txr_de")), "after the year: every 4 weeks at 9.69 + 19%");
        Assert.That(StripeRenewalSchedule.Marker(schedules.Single()), Is.EqualTo("l_premium:1153"));
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.EqualTo(1));
        Assert.That(await RefundMark("stripe", "in_switch"), Is.Null);
    }

    /// <summary>The webhook of the switch invoice (and its replays) publishes the purchase of the new term once, grants nothing again</summary>
    [Test]
    public async Task WebhookReplaysPublishTheSwitchOnceAndGrantNothingTwice()
    {
        var expiry = (await Access("sub_1")).Expires;
        await service.SwitchTerm("buyer", "sub_1", Request(52));
        var callback = Callback(service);

        for (var i = 0; i < 2; i++)
            await StripeExtensionTests.RecordPayment(callback, await service.StripeInvoicePaid(Invoice("in_switch", "subscription_update", 11863)), "stripe");

        Assert.That(await Grants("stripe-invoice-in_switch"), Is.EqualTo(1));
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry.AddSeconds(Year)));
        var payment = publishedPayments.Single();
        Assert.That((payment.SubscriptionIsTermSwitch, payment.SubscriptionIsRenewal, payment.SubscriptionBillingWeeks, payment.SubscriptionPreviousBillingWeeks),
            Is.EqualTo(((bool?)true, (bool?)false, (int?)52, (int?)4)));
        Assert.That((payment.PayedAmount, payment.SubscriptionRenewalAmount, payment.SubscriptionRenewalWeeks), Is.EqualTo((118.63, (decimal?)11.53m, (int?)4)));
        Assert.That((payment.DeclarationText, payment.SubscriptionPreviousAccessEndsAt, payment.ServiceEndsAtUtc),
            Is.EqualTo(("Switch now", (DateTime?)expiry, (DateTime?)expiry.AddSeconds(Year))));
        Assert.That(payment.SubscriptionNextBillingAt, Is.EqualTo(DateTime.UtcNow.AddDays(364)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(await PaymentRecords("stripe"), Is.EqualTo(1));
    }

    [Test]
    public async Task SwitchInvoiceWithAnotherAmountIsNotGranted()
    {
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeTermSwitchRequest request) =>
            {
                remote = Switched(request);
                return new StripeTermSwitch(remote, Invoice("in_switch", "subscription_update", 9999), null);
            });
        var expiry = (await Access("sub_1")).Expires;

        var result = await service.SwitchTerm("buyer", "sub_1", Request(52));

        Assert.That(result.Switched, Is.False);
        Assert.That(await Grants("stripe-invoice-in_switch"), Is.Zero);
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry));
        Assert.That(await RefundMark("stripe", "in_switch"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That((await db.SubscriptionTermSwitches.SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Failed));
    }

    /// <summary>
    /// A payment that needs the buyer's confirmation (SCA) is a pending update: nothing changes until it is paid, the same request
    /// returns the same page, another term is refused meanwhile; once paid the webhook grants it
    /// </summary>
    [Test]
    public async Task PaymentNeedingConfirmationSwitchesOnlyOnceItIsPaid()
    {
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot current, StripeTermSwitchRequest _) =>
            {
                remote = current with { HasPendingUpdate = true };
                return new StripeTermSwitch(remote, Invoice("in_switch", "subscription_update", 3176, "open"), "https://invoice.stripe.test/in_switch");
            });
        var expiry = (await Access("sub_1")).Expires;

        var result = await service.SwitchTerm("buyer", "sub_1", Request(13));

        Assert.That((result.Switched, result.Status, result.ApprovalUrl), Is.EqualTo((false, "payment_required", "https://invoice.stripe.test/in_switch")));
        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium"));
        Assert.That((await service.SwitchTerm("buyer", "sub_1", Request(13))).ApprovalUrl, Is.EqualTo("https://invoice.stripe.test/in_switch"));
        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message,
            Is.EqualTo(SubscriptionService.TermSwitchPending));
        Assert.That((await service.GetTermOptions("buyer", "sub_1")).Pending.Status, Is.EqualTo("payment_required"));

        remote = Switched(new StripeTermSwitchRequest(3176, "eur", 13, null, "l_premium-quarter", "13 weeks premium"));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_switch", "subscription_update", 3176)), Is.Not.Null);

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry.AddSeconds(Quarter)));
        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium-quarter"));
        Assert.That(switches, Is.Empty, "Stripe was called once (the setup above)");
    }

    /// <summary>The switch call's response was lost but Stripe applied it: its invoice is recognised without an invoice id</summary>
    [Test]
    public async Task LostResponseIsRecognisedByTheInvoiceWebhook()
    {
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .Returns((StripeSubscriptionSnapshot _, StripeTermSwitchRequest request) =>
            {
                remote = Switched(request);
                throw new TimeoutException("lost response");
            });

        var result = await service.SwitchTerm("buyer", "sub_1", Request(52));

        Assert.That((result.Switched, result.Status), Is.EqualTo((false, "processing")));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_lost", "subscription_update", 11863)), Is.Not.Null);
        Assert.That(await Grants("stripe-invoice-in_lost"), Is.EqualTo(1));
        Assert.That((await db.SubscriptionTermSwitches.SingleAsync()).InvoiceId, Is.EqualTo("in_lost"));
    }

    [Test]
    public async Task DeclinedSwitchFailsAndGivesThePromotionUseBack()
    {
        AddPromotion();
        await db.SaveChangesAsync();
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ThrowsAsync(new Stripe.StripeException { StripeError = new Stripe.StripeError { Type = "card_error", Message = "Your card was declined." } });

        var error = Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52, 59.31m)));

        Assert.That(error.Message, Is.EqualTo("The payment failed: Your card was declined."));
        Assert.That((await db.SubscriptionTermSwitches.SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Failed));
        db.ChangeTracker.Clear();
        Assert.That((await db.DiscountCodes.SingleAsync()).TimesRedeemed, Is.Zero);
        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52, 59.31m))).Message,
            Is.EqualTo("The payment failed: Your card was declined."), "a failed switch does not block a new attempt");
    }

    /// <summary>A full refund of the switch invoice takes back exactly the year it granted; the 4 weeks paid before stay</summary>
    [Test]
    public async Task RefundOfTheSwitchInvoiceRevertsOnlyItsGrant()
    {
        var expiry = (await Access("sub_1")).Expires;
        await service.SwitchTerm("buyer", "sub_1", Request(52));

        await service.StripeInvoiceRefunded(Invoice("in_switch", "subscription_update", 11863), 11863, 11863);

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_switch", "subscription_update", 11863)), Is.Null, "a refunded invoice is not granted again");
    }

    /// <summary>
    /// A chargeback of the switch invoice takes back exactly the year it granted and ends the subscription; winning it grants the
    /// year again once
    /// </summary>
    [Test]
    public async Task DisputeOfTheSwitchInvoiceRevertsItsGrantAndAWinRestoresIt()
    {
        var expiry = (await Access("sub_1")).Expires;
        await service.SwitchTerm("buyer", "sub_1", Request(52));
        stripe.Setup(s => s.ReleaseSchedule("sub_sched_1", "sub_1")).ReturnsAsync(() => remote = remote with { ScheduleId = null });
        stripe.Setup(s => s.CancelNow("sub_1")).ReturnsAsync(() => remote = remote with { Status = "canceled", EndedAt = DateTime.UtcNow });
        var invoice = Invoice("in_switch", "subscription_update", 11863);

        await service.StripeInvoiceDisputed(invoice);
        await service.StripeInvoiceDisputed(invoice);

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)), "only the year is taken back");
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        stripe.Verify(s => s.ReleaseSchedule("sub_sched_1", "sub_1"), Times.Once);
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("expired"));
        Assert.That(await service.StripeInvoicePaid(invoice), Is.Null, "a disputed invoice is not granted again");

        await service.StripeInvoiceDisputeWon(invoice);
        await service.StripeInvoiceDisputeWon(invoice);

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry.AddSeconds(Year)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await Grants("stripe-invoice-in_switch-disputewon"), Is.EqualTo(1));
        Assert.That(await service.StripeInvoicePaid(invoice), Is.Not.Null, "a redelivery after the win grants nothing more");
        Assert.That(await Grants("stripe-invoice-in_switch"), Is.EqualTo(1));
    }

    /// <summary>A 13 week first term can switch to 52 weeks: its renewal schedule is released first and made again for the year</summary>
    [Test]
    public async Task QuarterSwitchesToTheYearAndIsScheduledAgain()
    {
        await service.SwitchTerm("buyer", "sub_1", Request(13));
        var expiry = (await Access("sub_1")).Expires;
        stripe.Setup(s => s.ReleaseSchedule("sub_sched_1", "sub_1")).ReturnsAsync(() => remote = remote with { ScheduleId = null });
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot current, StripeTermSwitchRequest request) =>
            {
                Assert.That(current.ScheduleId, Is.Null, "released before the change");
                remote = Switched(request);
                return new StripeTermSwitch(remote, Invoice("in_year", "subscription_update", request.UnitAmount), null);
            });

        var options = await service.GetTermOptions("buyer", "sub_1");
        var result = await service.SwitchTerm("buyer", "sub_1", Request(52));

        Assert.That(options.Terms.Select(t => t.Weeks), Is.EqualTo(new[] { 52 }));
        Assert.That(result.Switched, Is.True);
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(expiry.AddSeconds(Year)));
        Assert.That(schedules.Select(StripeRenewalSchedule.Marker), Is.EqualTo(new[] { "l_premium:1153", "l_premium:1153" }));
        Assert.That((await Loaded()).Product.Slug, Is.EqualTo("l_premium-year"));
        Assert.That((await service.GetTermOptions("buyer", "sub_1")).UnavailableReason, Is.EqualTo("no_longer_term"));
    }

    /// <summary>Slot bundles switch like single plans: the bundle's tier slots get the year on top of their expiry</summary>
    [Test]
    public async Task SlotBundleSwitchesToTheYearBundle()
    {
        foreach (var (slug, price, seconds) in new[] { ("l_premium-slots-4", 15.69m, (long)FourWeeks), ("l_premium-slots-4-year", 149.69m, Year) })
        {
            var plan = new TopUpProduct
            {
                Slug = slug, Title = slug, Price = price, Cost = 3000, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
                OwnershipSeconds = seconds, Type = Product.ProductType.SERVICE, SlotCount = 2, SlotTier = "premium"
            };
            db.TopUpProducts.Add(plan);
            await db.SaveChangesAsync();
            await groups.AddProductToGroup(plan, "premium");
        }
        metadata[Keys.ProductSlug] = "l_premium-slots-4";
        metadata[Keys.PriceCents] = "1867";
        var bundle = Snapshot("prod_bundle", 1867, periodEnd) with { Id = "sub_2" };
        stripe.Setup(s => s.GetSubscription("sub_2")).ReturnsAsync(() => bundle);
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeTermSwitchRequest request) =>
            {
                bundle = Switched(request) with { Id = "sub_2" };
                return new StripeTermSwitch(bundle, Invoice("in_bundle", "subscription_update", request.UnitAmount) with { SubscriptionId = "sub_2" }, null);
            });
        Assert.That(await service.StripeInvoicePaid(Invoice("in_b1", "subscription_create", 1867) with { SubscriptionId = "sub_2" }), Is.Not.Null);
        var before = await db.TierSlots.AsNoTracking().Where(t => t.SubscriptionId == "sub_2").ToDictionaryAsync(t => t.Id, t => t.Expires);

        var options = await service.GetTermOptions("buyer", "sub_2");
        var result = await service.SwitchTerm("buyer", "sub_2", Request(52, 178.13m));

        Assert.That(options.Terms.Select(t => (t.ProductSlug, t.ChargedNow, t.RenewsAsProductSlug)),
            Is.EqualTo(new[] { ("l_premium-slots-4-year", 178.13m, "l_premium-slots-4") }), "149.69 + 19%, the single plans are not offered");
        Assert.That(result.Switched, Is.True);
        var slots = await db.TierSlots.AsNoTracking().Where(t => t.SubscriptionId == "sub_2").ToListAsync();
        Assert.That(slots.Select(t => (t.Tier, t.Expires)), Is.EqualTo(slots.Select(t => ("premium", before[t.Id].AddSeconds(Year)))));
        Assert.That(slots, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DisabledSwitchIsUnavailable()
    {
        var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["SUBSCRIPTIONS:TERM_SWITCH_ENABLED"] = "false" }).Build();
        var disabled = Service(stripe.Object, config: off);

        Assert.That((await disabled.GetTermOptions("buyer", "sub_1")).UnavailableReason, Is.EqualTo(SubscriptionService.TermSwitchUnavailable));
        Assert.That(Assert.ThrowsAsync<ApiException>(() => disabled.SwitchTerm("buyer", "sub_1", Request(52))).Message,
            Is.EqualTo(SubscriptionService.TermSwitchUnavailable));
        Assert.That(switches, Is.Empty);
    }

    [Test]
    public async Task CancelledSubscriptionCanNotSwitch()
    {
        var subscription = await Loaded();
        (subscription.Status, subscription.EndsAt) = ("cancelled", periodEnd);
        await db.SaveChangesAsync();

        Assert.That((await service.GetTermOptions("buyer", "sub_1")).UnavailableReason, Is.EqualTo("subscription_not_active"));
        Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52)));
        Assert.That(switches, Is.Empty);
    }

    [Test]
    public async Task PastDueSubscriptionCanNotSwitch()
    {
        (await Loaded()).Status = "past_due";
        await db.SaveChangesAsync();

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message, Is.EqualTo("subscription_not_active"));
    }

    [Test]
    public async Task BookedExtensionBlocksTheSwitch()
    {
        var subscription = await Loaded();
        (subscription.ExtensionStartsAt, subscription.ExtensionPriceCents) = (periodEnd, 11863);
        await db.SaveChangesAsync();

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message, Is.EqualTo("extension_booked"));
    }

    /// <summary>A pending plan change (an unpaid upgrade) at Stripe blocks the switch</summary>
    [Test]
    public void PendingPlanChangeBlocksTheSwitch()
    {
        remote = remote with { HasPendingUpdate = true };

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message, Is.EqualTo("subscription_not_switchable"));
        Assert.That(switches, Is.Empty);
    }

    [Test]
    public async Task ChangedPriceIsRefused()
    {
        AddPromotion();
        await db.SaveChangesAsync();

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52, 118.63m))).Message,
            Is.EqualTo(SubscriptionService.TermSwitchPriceChanged), "the buyer was shown another price");
        Assert.That(switches, Is.Empty);
    }

    [Test]
    public void InvalidDeclarationIsRefused()
    {
        var request = Request(52);
        request.Declaration.ImmediatePerformanceRequested = false;

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", request)).Message,
            Is.EqualTo("invalid_service_performance_declaration"));
    }

    /// <summary>The 13 week first term after a paid switch, with its renewal schedule sub_sched_1 (released again on request)</summary>
    private async Task SwitchToQuarter()
    {
        Assert.That((await service.SwitchTerm("buyer", "sub_1", Request(13))).Switched, Is.True);
        stripe.Setup(s => s.ReleaseSchedule(It.IsAny<string>(), "sub_1")).ReturnsAsync(() => remote = remote with { ScheduleId = null });
    }

    private void PendingSwitches() =>
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot current, StripeTermSwitchRequest request) =>
            {
                remote = current with { HasPendingUpdate = true };
                return new StripeTermSwitch(remote, Invoice("in_pending", "subscription_update", request.UnitAmount, "open"), "https://invoice.stripe.test/in_pending");
            });

    /// <summary>§ 309: a 13 week term's schedule is not released right before its renewal (Stripe could renew it for 13 weeks)</summary>
    [Test]
    public async Task SwitchIsRefusedRightBeforeTheRenewal()
    {
        var subscription = await Loaded();
        subscription.RenewsAt = DateTime.UtcNow.AddHours(12);
        await db.SaveChangesAsync();

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message, Is.EqualTo("renewal_due"));
        Assert.That((await service.GetTermOptions("buyer", "sub_1")).UnavailableReason, Is.EqualTo("renewal_due"));
        Assert.That(switches, Is.Empty);
    }

    /// <summary>A switch that failed at Stripe gives the 13 week term its renewal schedule back</summary>
    [Test]
    public async Task FailedSwitchSchedulesTheQuarterAgain()
    {
        await SwitchToQuarter();
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .ThrowsAsync(new Stripe.StripeException { StripeError = new Stripe.StripeError { Type = "card_error", Message = "Declined" } });

        Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52)));

        stripe.Verify(s => s.ReleaseSchedule("sub_sched_1", "sub_1"), Times.Once);
        Assert.That(schedules, Has.Count.EqualTo(2), "scheduled again after the failure");
        Assert.That((await Loaded()).RenewalScheduleId, Is.EqualTo("sub_sched_2"));
    }

    /// <summary>A switch waiting for the buyer's confirmation that expires unpaid stops blocking and the quarter is scheduled again</summary>
    [Test]
    public async Task ExpiredPendingSwitchSchedulesTheQuarterAgain()
    {
        await SwitchToQuarter();
        PendingSwitches();
        Assert.That((await service.SwitchTerm("buyer", "sub_1", Request(52))).Status, Is.EqualTo("payment_required"));
        Assert.That((await Loaded()).RenewalScheduleId, Is.Null, "released for the switch");

        remote = remote with { HasPendingUpdate = false };
        var expired = new Stripe.Event
        {
            Type = Stripe.EventTypes.CustomerSubscriptionPendingUpdateExpired,
            Data = new Stripe.EventData { Object = new Stripe.Subscription { Id = "sub_1" } }
        };
        var handle = typeof(global::Payments.Controllers.CallbackController).GetMethod("HandleStripeSubscriptionEvent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.That(await (Task<bool>)handle.Invoke(Callback(service), [expired])!, Is.True);

        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().OrderBy(r => r.Id).LastAsync()).Status, Is.EqualTo(TermSwitchStatus.Expired));
        Assert.That((await Loaded()).RenewalScheduleId, Is.EqualTo("sub_sched_2"));
        Assert.That((await service.GetTermOptions("buyer", "sub_1")).Pending, Is.Null);
    }

    /// <summary>The hourly run closes switches nobody paid and gives the promotion use back</summary>
    [Test]
    public async Task HourlyRunExpiresAbandonedSwitches()
    {
        AddPromotion();
        await db.SaveChangesAsync();
        PendingSwitches();
        await service.SwitchTerm("buyer", "sub_1", Request(52, 59.31m));
        var row = await db.SubscriptionTermSwitches.SingleAsync();
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        await service.ConvertDueRenewals();

        db.ChangeTracker.Clear();
        Assert.That((await db.SubscriptionTermSwitches.SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Expired));
        Assert.That((await db.DiscountCodes.SingleAsync()).TimesRedeemed, Is.Zero);
    }

    /// <summary>Stripe could not be read after a failed call: the buyer can retry after a short backoff, not after 23 hours</summary>
    [Test]
    public async Task UnreadableStripeBlocksOnlyBriefly()
    {
        var (unreadable, calls) = (false, 0);
        stripe.Setup(s => s.GetSubscription("sub_1")).Returns(() => unreadable
            ? Task.FromException<StripeSubscriptionSnapshot>(new TimeoutException("Stripe down")) : Task.FromResult(remote));
        stripe.Setup(s => s.SwitchTerm(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeTermSwitchRequest>()))
            .Returns(() => { unreadable = ++calls == 1; return Task.FromException<StripeTermSwitch>(new TimeoutException("lost")); });

        Assert.That((await service.SwitchTerm("buyer", "sub_1", Request(52))).Status, Is.EqualTo("processing"));

        var row = await db.SubscriptionTermSwitches.SingleAsync();
        Assert.That(row.ExpiresAt, Is.EqualTo(DateTime.UtcNow.AddMinutes(15)).Within(TimeSpan.FromMinutes(1)));
        unreadable = false;
        row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", Request(52))).Message,
            Is.EqualTo("The term could not be switched right now. Please try again later."), "reached Stripe again");
    }

    /// <summary>A refused declaration (its id was used for something else) leaves no pending switch and no used promotion</summary>
    [Test]
    public async Task ReusedDeclarationChangesNothing()
    {
        AddPromotion();
        var request = Request(52, 59.31m);
        db.ServicePerformanceDeclarations.Add(StripeExtensionTests.Evidence(1, "Other purchase", "de"));
        await db.SaveChangesAsync();
        var used = await db.ServicePerformanceDeclarations.SingleAsync();
        request.Declaration.RequestId = used.RequestId;

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", request)).Message,
            Is.EqualTo("service declaration request id already used"));

        Assert.That(await db.SubscriptionTermSwitches.CountAsync(), Is.Zero);
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.Zero);
        Assert.That(switches, Is.Empty);
    }

    /// <summary>The switch must name the price the buyer was shown</summary>
    [Test]
    public void ExpectedPriceIsRequired()
    {
        var request = Request(52);
        request.ExpectedChargedNow = null;

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "sub_1", request)).Message,
            Is.EqualTo(SubscriptionService.ExpectedPriceRequired));
    }

    /// <summary>"Renew for another term" is refused while a switch waits for its payment</summary>
    [Test]
    public async Task ExtensionIsRefusedWhileASwitchIsPending()
    {
        PendingSwitches();
        await service.SwitchTerm("buyer", "sub_1", Request(52));
        var withExtensions = Service(stripe.Object, config: ExtensionsOn);

        Assert.That(Assert.ThrowsAsync<ApiException>(() => withExtensions.ExtendLongTerm("buyer", "sub_1",
            new SubscriptionExtensionRequest { Declaration = StripeExtensionTests.Declaration() })).Message, Is.EqualTo(SubscriptionService.TermSwitchPending));
    }

    /// <summary>The confirmation names the access end as granted (it moved after the request), not the preview</summary>
    [Test]
    public async Task ConfirmationStatesTheActualAccessEnd()
    {
        PendingSwitches();
        await service.SwitchTerm("buyer", "sub_1", Request(52));
        var access = await Access("sub_1");
        access.Expires = access.Expires.AddDays(2);
        await db.SaveChangesAsync();
        var moved = access.Expires;
        remote = Switched(new StripeTermSwitchRequest(11863, "eur", 52, null, "l_premium-year", "52 weeks premium"));

        var outcome = await service.StripeInvoicePaid(Invoice("in_pending", "subscription_update", 11863));
        await StripeExtensionTests.RecordPayment(Callback(service), outcome, "stripe");

        var actual = (await Access("sub_1")).Expires;
        Assert.That(publishedPayments.Single().ServiceEndsAtUtc, Is.EqualTo(actual));
        Assert.That(actual, Is.EqualTo(moved.AddSeconds(Year)).Within(TimeSpan.FromSeconds(1)), "the moved access end plus the year");
    }

    /// <summary>
    /// Paid after the hourly run expired it (its promotion use given back): granted and completed, the promotion counted again
    /// </summary>
    [Test]
    public async Task SwitchPaidAfterItExpiredCountsThePromotionAgain()
    {
        AddPromotion();
        await db.SaveChangesAsync();
        PendingSwitches();
        await service.SwitchTerm("buyer", "sub_1", Request(52, 59.31m));
        await db.SubscriptionTermSwitches.ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        await service.ConvertDueRenewals();
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.Zero);

        remote = Switched(new StripeTermSwitchRequest(5931, "eur", 52, null, "l_premium-year", "52 weeks premium"));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_pending", "subscription_update", 5931)), Is.Not.Null);
        Assert.That(await service.StripeInvoicePaid(Invoice("in_pending", "subscription_update", 5931)), Is.Not.Null);

        Assert.That((await db.SubscriptionTermSwitches.AsNoTracking().SingleAsync()).Status, Is.EqualTo(TermSwitchStatus.Completed));
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.EqualTo(1), "counted again once");
        Assert.That(await Grants("stripe-invoice-in_pending"), Is.EqualTo(1));
    }

    /// <summary>
    /// The missing price is answered with the same error slug as every other refusal, so the request model must not let ASP.NET's
    /// model validation answer first (with another error shape)
    /// </summary>
    [Test]
    public void ExpectedPriceIsCheckedByTheServiceNotByModelValidation()
    {
        var property = typeof(SubscriptionTermSwitchRequest).GetProperty(nameof(SubscriptionTermSwitchRequest.ExpectedChargedNow))!;

        Assert.That(property.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), true), Is.Empty);
    }

    /// <summary>
    /// Regression: SkyApi passes the shown price through a double, so it may arrive as 19.769999999999999; it is compared in cents
    /// </summary>
    [TestCase("19.769999999999999", 1977)]
    [TestCase("19.770000000000003", 1977)]
    [TestCase("57.520000000000003", 5752)]
    [TestCase("210.75000000000003", 21075)]
    [TestCase("210.74999999999997", 21075)]
    [TestCase("118.63", 11863)]
    public void PriceThroughADoubleGivesTheIntendedCents(string sent, long cents)
    {
        var request = Newtonsoft.Json.JsonConvert.DeserializeObject<SubscriptionTermSwitchRequest>($"{{\"weeks\":52,\"expectedChargedNow\":{sent}}}");

        Assert.That(StripeAmounts.ToMinorUnits(request.ExpectedChargedNow.Value, "EUR"), Is.EqualTo(cents));
        Assert.That(StripeAmounts.ToMinorUnits((decimal)(double)(cents / 100m), "EUR"), Is.EqualTo(cents));
    }

    [Test]
    public async Task SwitchAcceptsTheShownPriceAfterADoubleRoundTrip()
    {
        var result = await service.SwitchTerm("buyer", "sub_1", Request(52, decimal.Parse("118.62999999999999", System.Globalization.CultureInfo.InvariantCulture)));

        Assert.That(result.Switched, Is.True);
    }
}
