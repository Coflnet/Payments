using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
/// Owner decision: before a 52 week Stripe term ends the buyer can opt in to one more 52 week term instead of continuing every
/// 4 weeks; the renewal schedule then bills the long plan once more before the 4-week phase. The purchase confirmations carry the
/// buyer's early performance declaration (lawyer decision).
/// </summary>
public class StripeExtensionTests : DirectSubscriptionTestBase
{
    private const long Year = 31536000;
    private const string Refund = nameof(PaymentRequest.Status.REFUND_PENDING);
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private Dictionary<string, string> checkoutMetadata;
    private TopUpProduct premiumYear;
    private readonly List<StripeRenewalRequest> schedules = [];
    private readonly List<string> calls = [];

    [SetUp]
    public async Task SetUpYearPlan()
    {
        premiumYear = new TopUpProduct
        {
            Slug = "l_premium-year", Title = "52 weeks premium", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = Year, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(premiumYear);
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(premiumYear, "premium");
        periodEnd = Seconds(DateTime.UtcNow.AddDays(30));
        checkoutMetadata = new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_premium-year", [Keys.PriceCents] = "11863", [Keys.Mode] = "direct",
            [Keys.Country] = "DE", [Keys.RenewalProductSlug] = "l_premium", [Keys.RenewalPriceCents] = "1153"
        };
        remote = Snapshot();
        schedules.Clear();
        calls.Clear();
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        stripe.Setup(s => s.EnsureVatTaxRate("DE", 1900)).ReturnsAsync("txr_de");
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripeRenewalRequest request) =>
            {
                schedules.Add(request);
                calls.Add("schedule");
                remote = remote with { ScheduleId = "sub_sched_" + schedules.Count };
                return remote.ScheduleId;
            });
        stripe.Setup(s => s.ReleaseSchedule(It.IsAny<string>(), "sub_1")).ReturnsAsync((string id, string _) =>
        {
            calls.Add("release " + id);
            return remote = remote with { ScheduleId = null };
        });
    }

    private static DateTime Seconds(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private StripeSubscriptionSnapshot Snapshot(string plan = null, long priceCents = 11863, string scheduleId = null,
        DateTime? currentPeriodEnd = null, bool managed = false) =>
        new("sub_1", "active", false, null, currentPeriodEnd ?? periodEnd, null, "cus_1",
            new Dictionary<string, string>(checkoutMetadata), "si_1", "price_1",
            plan == null ? "prod_checkout" : StripePlans.ProductId(plan), priceCents, "eur", managed ? "exclusive" : null, managed,
            DateTime.UtcNow, false, scheduleId);

    private static StripeInvoiceInfo Invoice(string id = "in_1", string reason = "subscription_create", long total = 11863,
        DateTime? created = null) =>
        new(id, "sub_1", reason, "paid", total, total, "eur", created ?? DateTime.UtcNow, "DE", "10115", "buyer@example.test",
            "Buyer", new Dictionary<string, string>());

    private Task<UserSubscription> Loaded() =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.RenewalProduct).SingleAsync(s => s.ExternalId == "sub_1");

    internal static string Sha(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static SubscriptionDeclaration Declaration(string text = "Start now", string locale = "de") => new()
    {
        RequestId = Guid.NewGuid().ToString(), ImmediatePerformanceRequested = true, WithdrawalConsequenceAcknowledged = true,
        Locale = locale, DeclarationVersion = "v2", DeclarationText = text, DeclarationSha256 = Sha(text), AgreementId = "agreement",
        AgreementHash = Sha("agreement"), WithdrawalVersion = "w1", WithdrawalSha256 = Sha("withdrawal")
    };

    private static SubscriptionExtensionRequest Extension(SubscriptionDeclaration declaration = null) =>
        new() { Declaration = declaration ?? Declaration() };

    /// <summary>The year the checkout sold is paid and scheduled to continue every 4 weeks</summary>
    private async Task<SubscriptionService> PaidYear()
    {
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        return service;
    }

    [Test]
    public async Task ExtensionAddsOneYearPhaseBeforeTheFourWeekPhase()
    {
        var service = await PaidYear();

        var result = await service.ExtendLongTerm("buyer", "sub_1", Extension());

        Assert.That((result.Extended, result.ApprovalUrl, result.TermEndsAt), Is.EqualTo((true, (string)null, periodEnd)));
        Assert.That(result.ExtendedUntil, Is.EqualTo(periodEnd.AddDays(364)));
        Assert.That(schedules.Last(), Is.EqualTo(new StripeRenewalRequest(1153, "eur", null, "l_premium", "4 weeks premium", periodEnd,
            "txr_de", new StripeExtensionTerm(11863, "l_premium-year", "52 weeks premium", 52))),
            "the year again at 99.69 + 19% VAT, then 4 weeks at 9.69 + 19%");
        var subscription = await Loaded();
        Assert.That((subscription.ExtensionStartsAt, subscription.ExtensionPriceCents, subscription.RenewalScheduleId),
            Is.EqualTo(((DateTime?)periodEnd, (long?)11863, "sub_sched_2")));
        var declaration = await db.ServicePerformanceDeclarations.SingleAsync();
        Assert.That(declaration.PurchaseReference, Is.EqualTo(SubscriptionDeclarationService.ExtensionReference("sub_1", periodEnd)));
        Assert.That((declaration.StartsAtUtc, declaration.ProductSlug), Is.EqualTo((periodEnd, "l_premium-year")));
    }

    /// <summary>
    /// Creator code and "forever" code apply with the amount of the plan's own period (like the renewal price), a "once" code not
    /// </summary>
    [TestCase(DiscountCode.EveryRenewal, 5338L)] // 99.69 - 10% creator = 89.72, -50% (52-week amount) = 44.86, + 19% VAT
    [TestCase(DiscountCode.FirstPaymentOnly, 10677L)] // 89.72 + 19% VAT: the first payment code is used up
    public async Task ExtensionPriceKeepsTheLastingCodes(string duration, long cents)
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 10, IsActive = true });
        db.DiscountCodes.Add(new DiscountCode { Code = "BF2026", AmountType = "percent", Amount = 10, Duration = duration,
            PeriodAmounts = new() { [4] = 25, [52] = 50 } });
        checkoutMetadata[Keys.CreatorCode] = "CREATOR";
        checkoutMetadata[Keys.DiscountCode] = "BF2026";
        checkoutMetadata[Keys.PriceCents] = "5338";
        remote = Snapshot(priceCents: 5338);
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice(total: 5338)), Is.Not.Null);

        await service.ExtendLongTerm("buyer", "sub_1", Extension());

        Assert.That(schedules.Last().Extension.UnitAmount, Is.EqualTo(cents));
        Assert.That((await Loaded()).ExtensionPriceCents, Is.EqualTo(cents));
    }

    [Test]
    public async Task ManagedPaymentsExtensionIsPricedNet()
    {
        checkoutMetadata[Keys.Mode] = "managed";
        checkoutMetadata[Keys.PriceCents] = "9969";
        checkoutMetadata[Keys.RenewalPriceCents] = "969";
        remote = Snapshot(priceCents: 9969, managed: true);
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice(total: 11863)), Is.Not.Null);

        await service.ExtendLongTerm("buyer", "sub_1", Extension());

        Assert.That(schedules.Last().Extension.UnitAmount, Is.EqualTo(9969), "Stripe adds the tax");
        Assert.That(schedules.Last().TaxRateId, Is.Null);
    }

    /// <summary>
    /// The extension invoice pays the year at the extension price: it is granted as a year, does not convert the subscription,
    /// and carries the extension's declaration. The 4-week invoice after it converts the subscription as before.
    /// </summary>
    [TestCase("subscription_update")]
    [TestCase("subscription_cycle")]
    public async Task ExtensionInvoiceGrantsAYearAndTheFourWeekInvoiceAfterItConverts(string reason)
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension(Declaration("Extend now")));
        var extendedEnd = periodEnd.AddDays(364);
        remote = Snapshot(plan: "l_premium-year", priceCents: 11863, scheduleId: "sub_sched_2", currentPeriodEnd: extendedEnd);

        var extension = await service.StripeInvoicePaid(Invoice("in_2", reason, 11863, periodEnd));

        Assert.That(extension, Is.Not.Null);
        Assert.That((extension.Product.Slug, extension.IsExtension, extension.IsRenewal), Is.EqualTo(("l_premium-year", true, true)));
        Assert.That(extension.Declaration.DeclarationText, Is.EqualTo("Extend now"));
        var subscription = await Loaded();
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium-year"), "not converted to the 4-week plan");
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
        Assert.That(await RefundMark("stripe", "in_2"), Is.Null);
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(2 * Year)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(schedules, Has.Count.EqualTo(2), "the schedule with the 4-week phase stays");

        remote = Snapshot(plan: "l_premium", priceCents: 1153, scheduleId: "sub_sched_2", currentPeriodEnd: extendedEnd.AddDays(28));
        var fourWeeks = await service.StripeInvoicePaid(Invoice("in_3", "subscription_update", 1153, extendedEnd));

        Assert.That((fourWeeks.Product.Slug, fourWeeks.IsExtension, fourWeeks.Declaration), Is.EqualTo(("l_premium", false, (ServicePerformanceDeclaration)null)));
        Assert.That(((await Loaded()).Product.Slug, (await Loaded()).PriceCents), Is.EqualTo(("l_premium", 1153L)));
    }

    [Test]
    public async Task ExtensionInvoiceAtAnotherPriceIsMarkedForRefund()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension());
        remote = Snapshot(plan: "l_premium-year", priceCents: 11863, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364));

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 9999, periodEnd)), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_2"), Is.Zero);
        Assert.That((await RefundMark("stripe", "in_2")).ToString(), Is.EqualTo(Refund));
    }

    [Test]
    public async Task ASecondExtensionOfTheSameTermIsRefused()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension());

        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension()));
        Assert.That(schedules, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task NoExtensionWithinTheLastDayOfTheTerm()
    {
        periodEnd = Seconds(DateTime.UtcNow.AddHours(23));
        remote = Snapshot();
        var service = await PaidYear();

        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension()));
        Assert.That((await Loaded()).ExtensionStartsAt, Is.Null);
    }

    [Test]
    public async Task ExtensionWithoutAValidDeclarationIsRefused()
    {
        var service = await PaidYear();
        var invalid = Declaration();
        invalid.DeclarationSha256 = Sha("another text");
        var notRequested = Declaration();
        notRequested.ImmediatePerformanceRequested = notRequested.WithdrawalConsequenceAcknowledged = false;

        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension(invalid)));
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension(notRequested)));
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", new SubscriptionExtensionRequest()));
        Assert.That(schedules, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FourWeekAndLemonSqueezySubscriptionsCanNotBeExtended()
    {
        var user = await users.GetOrCreate("buyer");
        db.Subscriptions.AddRange(
            new UserSubscription { User = user, Product = premium, ProviderSlug = "stripe", ExternalId = "sub_4w", Status = "active", RenewsAt = periodEnd },
            new UserSubscription { User = user, Product = premiumYear, RenewalProduct = premium, RenewalPriceCents = 1153,
                ProviderSlug = "lemonsqueezy", ExternalId = "ls_1", Status = "active", RenewsAt = periodEnd });
        await db.SaveChangesAsync();
        var service = Service(stripe.Object);

        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_4w", Extension()));
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "ls_1", Extension()));
    }

    /// <summary>
    /// Cancelling releases the renewal schedule and with it the booked extension; a resumed subscription continues every 4 weeks
    /// </summary>
    [Test]
    public async Task CancellingDropsTheBookedExtension()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension());
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd });
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = remote with { CancelAtPeriodEnd = false, CancelAt = null });

        await service.CancelSubscription("buyer", "sub_1");

        Assert.That(calls, Does.Contain("release sub_sched_2"));
        var subscription = await Loaded();
        Assert.That((subscription.ExtensionStartsAt, subscription.ExtensionPriceCents), Is.EqualTo(((DateTime?)null, (long?)null)));

        await service.ResumeSubscription("buyer", "sub_1");
        Assert.That(schedules.Last().Extension, Is.Null);
    }

    [Test]
    public async Task PlanChangesWaitUntilTheBookedTermStarted()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension());

        var refused = Assert.ThrowsAsync<ApiException>(() => service.ChangePlan("buyer", "sub_1", "l_prem_plus-year"));
        Assert.That(refused.Message, Does.Contain("another term is booked"));
    }

    /// <summary>The subscription list shows the current term, the 4-week price and the extension offer, then the booking</summary>
    [Test]
    public async Task SubscriptionListDescribesTheTermAndTheExtension()
    {
        var service = await PaidYear();

        var offered = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That((offered.CurrentTermEndsAt, offered.CanExtendLongTerm, offered.ExtensionPrice, offered.RenewalPrice),
            Is.EqualTo((periodEnd, true, 118.63m, 11.53m)));
        Assert.That((offered.ExtensionTaxTreatment, offered.ExtendedUntil), Is.EqualTo(("included", (DateTime?)null)));

        await service.ExtendLongTerm("buyer", "sub_1", Extension());
        db.ChangeTracker.Clear();
        var booked = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That((booked.CanExtendLongTerm, booked.ExtensionPrice, booked.ExtendedUntil),
            Is.EqualTo((false, 118.63m, (DateTime?)periodEnd.AddDays(364))));
    }

    /// <summary>
    /// The confirmation of the purchase repeats the early performance declaration of the checkout with the paid term; renewals
    /// only name the locale
    /// </summary>
    [Test]
    public async Task FirstPaymentEventCarriesTheCheckoutDeclarationAndRenewalsDoNot()
    {
        var request = await SeedCheckout("stripe", "cs_1", premiumYear, 118.63m);
        db.ServicePerformanceDeclarations.Add(Evidence(request.Id, "Start my year now", "en"));
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.PaymentRequestId] = request.Id.ToString();
        remote = Snapshot();
        var service = Service(stripe.Object);

        await Record(service, await service.StripeInvoicePaid(Invoice()));

        var first = publishedPayments.Single();
        Assert.That((first.DeclarationVersion, first.DeclarationText, first.LegalLocale), Is.EqualTo(("v2", "Start my year now", "en")));
        Assert.That(first.ServiceStartsAtUtc, Is.EqualTo(Invoice().Created).Within(TimeSpan.FromMinutes(1)));
        Assert.That(first.ServiceEndsAtUtc, Is.EqualTo(periodEnd));
        Assert.That((first.SubscriptionIsExtension, first.SubscriptionExternalId), Is.EqualTo(((bool?)null, "sub_1")));

        remote = Snapshot(plan: "l_premium", priceCents: 1153, scheduleId: "sub_sched_1", currentPeriodEnd: periodEnd.AddDays(28));
        await Record(service, await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 1153, periodEnd)));

        var renewal = publishedPayments.Last();
        Assert.That((renewal.DeclarationText, renewal.ServiceStartsAtUtc, renewal.LegalLocale), Is.EqualTo(((string)null, (DateTime?)null, "en")));
    }

    [Test]
    public async Task ExtensionPaymentEventCarriesTheExtensionDeclaration()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension(Declaration("One more year", "en")));
        remote = Snapshot(plan: "l_premium-year", priceCents: 11863, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364));

        await Record(service, await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 11863, periodEnd)));

        var payment = publishedPayments.Single();
        Assert.That((payment.SubscriptionIsExtension, payment.SubscriptionIsRenewal, payment.DeclarationText, payment.LegalLocale),
            Is.EqualTo(((bool?)true, (bool?)true, "One more year", "en")));
        Assert.That((payment.ServiceStartsAtUtc, payment.ServiceEndsAtUtc), Is.EqualTo(((DateTime?)periodEnd, (DateTime?)periodEnd.AddDays(364))));
        Assert.That(payment.SubscriptionBillingWeeks, Is.EqualTo(52));
    }

    /// <summary>A first payment without a stored declaration is still confirmed (and logged)</summary>
    [Test]
    public async Task FirstPaymentWithoutADeclarationIsStillPublished()
    {
        var service = Service(stripe.Object);
        await Record(service, await service.StripeInvoicePaid(Invoice()));

        var payment = publishedPayments.Single();
        Assert.That((payment.DeclarationText, payment.ServiceStartsAtUtc, payment.LegalLocale), Is.EqualTo(((string)null, (DateTime?)null, (string)null)));
    }

    private async Task<TopUpProduct> PremiumPlusYear()
    {
        var plan = new TopUpProduct
        {
            Slug = "l_prem_plus-year", Title = "52 weeks prem+", Price = 369.69m, Cost = 90000, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = Year, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(plan);
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(plan, "premium_plus");
        await groups.AddProductToGroup(plan, "premium");
        return plan;
    }

    /// <summary>The extension year was booked and its invoice (in_2) granted</summary>
    private async Task<SubscriptionService> ExtensionPaid(bool managed = false)
    {
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        await service.ExtendLongTerm("buyer", "sub_1", Extension());
        var cents = managed ? 9969 : 11863;
        remote = Snapshot(plan: "l_premium-year", priceCents: cents, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364), managed: managed);
        Assert.That((await service.StripeInvoicePaid(Invoice("in_2", "subscription_update", 11863, periodEnd))).IsExtension, Is.True);
        return service;
    }

    private void ManagedCheckout()
    {
        checkoutMetadata[Keys.Mode] = "managed";
        checkoutMetadata[Keys.PriceCents] = "9969";
        checkoutMetadata[Keys.RenewalPriceCents] = "969";
        remote = Snapshot(priceCents: 9969, managed: true);
    }

    /// <summary>
    /// Regression (review): an upgrade's proration created shortly after the extension started looked like the extension's
    /// invoice (billing reason "updated", the long plan billed, created around the term end): Managed Payments granted a whole
    /// extra year for the proration
    /// </summary>
    [Test]
    public async Task UpgradeProrationRightAfterTheExtensionStartedGrantsNoTerm()
    {
        var plus = await PremiumPlusYear();
        ManagedCheckout();
        var service = await ExtensionPaid(managed: true);
        var subscription = await db.Subscriptions.SingleAsync(s => s.ExternalId == "sub_1");
        subscription.Product = plus; // the paid upgrade was applied
        await db.SaveChangesAsync();
        remote = Snapshot(plan: "l_prem_plus-year", priceCents: 36969, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364), managed: true);

        var proration = await service.StripeInvoicePaid(Invoice("in_up", "subscription_update", 500, periodEnd.AddHours(1)));

        Assert.That((proration.IsProration, proration.IsExtension), Is.EqualTo((true, false)));
        Assert.That(await Grants("stripe-invoice-in_up"), Is.Zero);
    }

    /// <summary>Regression (review): a direct upgrade's proration was checked against the extension price and marked for a refund</summary>
    [Test]
    public async Task DirectUpgradeProrationAfterTheExtensionIsRecordedAsAProration()
    {
        var plus = await PremiumPlusYear();
        var service = await ExtensionPaid();
        var subscription = await db.Subscriptions.SingleAsync(s => s.ExternalId == "sub_1");
        subscription.Product = plus;
        db.SubscriptionPlanChanges.Add(new SubscriptionPlanChange { SubscriptionId = "sub_1", PreviousProduct = premiumYear, Product = plus,
            ChangedAt = periodEnd.AddHours(1), PeriodEnd = periodEnd.AddDays(364), InvoiceId = "in_up" });
        await db.SaveChangesAsync();
        remote = Snapshot(plan: "l_prem_plus-year", priceCents: 11863, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364));

        var proration = await service.StripeInvoicePaid(Invoice("in_up", "subscription_update", 1234, periodEnd.AddHours(1)));

        Assert.That((proration.IsProration, proration.IsExtension), Is.EqualTo((true, false)));
        Assert.That(await RefundMark("stripe", "in_up"), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_up"), Is.Zero);
    }

    [Test]
    public async Task PlanChangesWaitTwoDaysAfterTheExtensionStarted()
    {
        await PaidYear();
        var subscription = await db.Subscriptions.SingleAsync();
        subscription.ExtensionStartsAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        Assert.That(SubscriptionService.ExtensionBlocksPlanChange(subscription, DateTime.UtcNow), Is.True);
        Assert.That(SubscriptionService.ExtensionBlocksPlanChange(subscription, DateTime.UtcNow.AddDays(1.5)), Is.False);
        Assert.ThrowsAsync<ApiException>(() => Service(stripe.Object).ChangePlan("buyer", "sub_1", "l_prem_plus-year"));
    }

    /// <summary>Regression (review): a refunded extension invoice (billing reason "updated") reverted nothing</summary>
    [Test]
    public async Task RefundedExtensionInvoiceRevokesTheExtensionYear()
    {
        var service = await ExtensionPaid();
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(2 * Year)).Within(TimeSpan.FromMinutes(1)));

        await service.StripeInvoiceRefunded(Invoice("in_2", "subscription_update", 11863, periodEnd), 11863, 11863);

        db.ChangeTracker.Clear();
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(Year)).Within(TimeSpan.FromMinutes(1)));
    }

    /// <summary>The booked price is stored before Stripe is asked, so the extension's invoice is always recognised</summary>
    [Test]
    public async Task BookingIsStoredBeforeStripeIsCalled()
    {
        var service = await PaidYear();
        long? storedBeforeStripe = null;
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ReturnsAsync(() =>
            {
                storedBeforeStripe = db.Subscriptions.AsNoTracking().Single(s => s.ExternalId == "sub_1").ExtensionPriceCents;
                return "sub_sched_2";
            });

        await service.ExtendLongTerm("buyer", "sub_1", Extension());

        Assert.That(storedBeforeStripe, Is.EqualTo(11863));
    }

    [Test]
    public async Task FailedStripeCallDropsTheBooking()
    {
        var service = await PaidYear();
        stripe.Setup(s => s.ScheduleRenewal(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripeRenewalRequest>()))
            .ThrowsAsync(new Stripe.StripeException("down"));

        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension()));

        db.ChangeTracker.Clear();
        var subscription = await Loaded();
        Assert.That((subscription.ExtensionStartsAt, subscription.ExtensionPriceCents, subscription.RenewalScheduleId),
            Is.EqualTo(((DateTime?)null, (long?)null, "sub_sched_1")));
        Assert.That((await service.GetUserSubscriptions("buyer")).Single().CanExtendLongTerm, Is.True, "can be booked again");
    }

    /// <summary>Regression (review): a subscription cancelled at Stripe (not through us) still showed its extension</summary>
    [Test]
    public async Task ExtensionOfASubscriptionCancelledElsewhereIsNotShown()
    {
        var service = await PaidYear();
        await service.ExtendLongTerm("buyer", "sub_1", Extension());
        Assert.That((await service.GetUserSubscriptions("buyer")).Single().ExtendedUntil, Is.Not.Null);
        remote = remote with { CancelAtPeriodEnd = true, CancelAt = periodEnd };

        await service.StripeSubscriptionChanged("sub_1");

        db.ChangeTracker.Clear();
        var listed = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That((listed.ExtendedUntil, listed.ExtensionStartsAt), Is.EqualTo(((DateTime?)null, (DateTime?)null)));
    }

    [Test]
    public async Task ExtensionsAreBookableOnlyWithinTheBookingWindow()
    {
        periodEnd = Seconds(DateTime.UtcNow.AddDays(61));
        remote = Snapshot();
        var service = await PaidYear();

        Assert.That((await service.GetUserSubscriptions("buyer")).Single().CanExtendLongTerm, Is.False);
        Assert.ThrowsAsync<ApiException>(() => service.ExtendLongTerm("buyer", "sub_1", Extension()));

        var wider = Service(stripe.Object, config: new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["SUBSCRIPTIONS:EXTENSIONS_ENABLED"] = "true", ["SUBSCRIPTIONS:EXTENSION_BOOKING_WINDOW_DAYS"] = "90"
            }).Build());
        Assert.That((await wider.ExtendLongTerm("buyer", "sub_1", Extension())).Extended, Is.True);
    }

    private static readonly IConfiguration ExtensionsOff = new ConfigurationBuilder().Build();

    /// <summary>Until tried in Stripe test mode and the PayPal sandbox, new bookings are off by default</summary>
    [Test]
    public async Task NewExtensionsAreOffByDefault()
    {
        await PaidYear();
        var off = Service(stripe.Object, config: ExtensionsOff);

        Assert.That((await off.GetUserSubscriptions("buyer")).Single().CanExtendLongTerm, Is.False);
        var refused = Assert.ThrowsAsync<ApiException>(() => off.ExtendLongTerm("buyer", "sub_1", Extension()));
        Assert.That(refused.Message, Is.EqualTo("extension_unavailable"));
        Assert.That(schedules, Has.Count.EqualTo(1));
        Assert.That(await db.ServicePerformanceDeclarations.CountAsync(), Is.Zero);
    }

    /// <summary>Only new bookings are gated: one booked while the switch was on is billed, granted and shown as before</summary>
    [Test]
    public async Task BookedExtensionKeepsWorkingWithTheSwitchOff()
    {
        var on = await PaidYear();
        await on.ExtendLongTerm("buyer", "sub_1", Extension());
        var off = Service(stripe.Object, config: ExtensionsOff);
        db.ChangeTracker.Clear();
        Assert.That((await off.GetUserSubscriptions("buyer")).Single().ExtendedUntil, Is.EqualTo(periodEnd.AddDays(364)));
        remote = Snapshot(plan: "l_premium-year", priceCents: 11863, scheduleId: "sub_sched_2", currentPeriodEnd: periodEnd.AddDays(364));

        var extension = await off.StripeInvoicePaid(Invoice("in_2", "subscription_update", 11863, periodEnd));

        Assert.That((extension.IsExtension, extension.Product.Slug), Is.EqualTo((true, "l_premium-year")));
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
    }

    internal static ServicePerformanceDeclaration Evidence(int paymentRequestId, string text, string locale) => new()
    {
        RequestId = Guid.NewGuid().ToString(), UserId = "buyer", ProductId = 1, ProductSlug = "plan", Count = 1,
        PurchaseReference = SubscriptionDeclarationService.CheckoutReference(paymentRequestId), Locale = locale,
        DeclarationVersion = "v2", DeclarationText = text, DeclarationSha256 = Sha(text), AgreementId = "agreement",
        AgreementHash = Sha("agreement"), WithdrawalVersion = "w1", WithdrawalSha256 = Sha("withdrawal"),
        EarlyPerformanceRequested = true, WithdrawalConsequenceAcknowledged = true, PaymentRequestId = paymentRequestId,
        CreatedAtUtc = DateTime.UtcNow
    };

    /// <summary>Publishes and records an outcome as the webhook does</summary>
    private Task Record(SubscriptionService service, SubscriptionPaymentOutcome outcome) => RecordPayment(Callback(service), outcome, "stripe");

    internal static Task RecordPayment(global::Payments.Controllers.CallbackController controller, SubscriptionPaymentOutcome outcome, string provider)
    {
        var record = typeof(global::Payments.Controllers.CallbackController)
            .GetMethod("RecordSubscriptionPayment", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)record.Invoke(controller, [outcome, provider])!;
    }
}
