using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

public class StripeSubscriptionTests : DirectSubscriptionTestBase
{
    private const string SigningSecret = "whsec_test";
    private const string Refund = nameof(PaymentRequest.Status.REFUND_PENDING);
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private Dictionary<string, string> checkoutMetadata;

    [SetUp]
    public void SetUpStripe()
    {
        periodEnd = DateTime.UtcNow.AddDays(28);
        checkoutMetadata = new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_premium", [Keys.PriceCents] = "969", [Keys.Mode] = "direct",
            // US: no VAT, so the prices of these tests are the plan prices; VAT cases set another country
            [Keys.Country] = "US"
        };
        remote = Snapshot();
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
    }

    /// <param name="plan">null: the checkout's inline product (plan from metadata); otherwise the plan product a switch moved to</param>
    private StripeSubscriptionSnapshot Snapshot(string status = "active", bool cancelAtPeriodEnd = false, bool managed = false,
        string plan = null, long priceCents = 969, DateTime? cancelAt = null, bool pending = false)
    {
        if (managed)
            checkoutMetadata[Keys.Mode] = "managed";
        return new("sub_1", status, cancelAtPeriodEnd, cancelAt ?? (cancelAtPeriodEnd ? periodEnd : null), periodEnd,
            status == "canceled" ? DateTime.UtcNow : null, "cus_1", new Dictionary<string, string>(checkoutMetadata),
            "si_1", "price_1", plan == null ? "prod_checkout" : StripePlans.ProductId(plan), priceCents, "eur",
            managed ? "inclusive" : null, managed, DateTime.UtcNow, pending);
    }

    private static StripeInvoiceInfo Invoice(string id = "in_1", string reason = "subscription_create", long total = 969,
        string currency = "eur", string country = "DE") =>
        new(id, "sub_1", reason, "paid", total, total, currency, DateTime.UtcNow, country, "10115", "buyer@example.test",
            "Buyer", new Dictionary<string, string>());

    private void OnPriceChange(Func<StripePriceChangeRequest, StripePriceChange> result, List<StripePriceChangeRequest> requests = null) =>
        stripe.Setup(s => s.ChangePrice(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripePriceChangeRequest>()))
            .ReturnsAsync((StripeSubscriptionSnapshot _, StripePriceChangeRequest r) =>
            {
                requests?.Add(r);
                return result(r);
            });

    [Test]
    public async Task InvoicePaidBeforeCheckoutCompletedRecordsTheSubscriptionAndGrantsOnce()
    {
        var service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null);
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Not.Null, "a redelivery returns the payment again so recording can be retried");

        var subscription = await Subscription("sub_1");
        Assert.That(subscription.ProviderSlug, Is.EqualTo("stripe"));
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(subscription.PriceCents, Is.EqualTo(969));
        Assert.That(subscription.CheckoutCountry, Is.EqualTo("US"), "the US direct cap counts by it");
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That(await Grants("stripe-invoice-in_1-topup"), Is.EqualTo(1));
        var access = await Access("sub_1");
        Assert.That(access.Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(access.Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(FourWeeks)).Within(TimeSpan.FromMinutes(1)));
        Assert.That((await Buyer()).Balance, Is.Zero, "the credited coins are spent on the service");
    }

    /// <summary>
    /// A slot bundle bought with a discount code is granted as four seats bound to the subscription, and renews them.
    /// </summary>
    [Test]
    public async Task DiscountedSlotBundleGrantsFourSeatsAndRenewsThem()
    {
        var bundle = new TopUpProduct { Slug = "l_premium-slots-4", Title = "slots", Price = 33.69m, Cost = 7200, CurrencyCode = "eur",
            ProviderSlug = "lemonsqueezy", OwnershipSeconds = FourWeeks, SlotCount = 4, SlotTier = "premium", Type = Product.ProductType.SERVICE };
        db.TopUpProducts.Add(bundle);
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(bundle, bundle.Slug);
        await groups.AddProductToGroup(bundle, "premium");
        checkoutMetadata[Keys.ProductSlug] = bundle.Slug;
        checkoutMetadata[Keys.DiscountCode] = "BF2026";
        checkoutMetadata[Keys.PriceCents] = "2527";
        remote = Snapshot(priceCents: 2527);
        var service = Service(stripe.Object);

        Assert.That(await service.StripeInvoicePaid(Invoice(total: 2527)), Is.Not.Null);
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 2527)), Is.Not.Null);

        var buyer = await Buyer();
        var seats = await db.TierSlots.Where(t => t.UserId == buyer.Id && t.SubscriptionId == "sub_1").ToListAsync();
        Assert.That(seats, Has.Count.EqualTo(4));
        Assert.That(seats.Select(t => t.Tier), Is.All.EqualTo("premium"));
        Assert.That(seats.Select(t => t.Expires), Is.All.EqualTo(DateTime.UtcNow.AddSeconds(2 * FourWeeks)).Within(TimeSpan.FromMinutes(1)));
        Assert.That((await Subscription("sub_1")).DiscountCode, Is.EqualTo("BF2026"));
    }

    /// <summary>
    /// A plan switch reprices with the period amount of the target plan, not the code default.
    /// </summary>
    [Test]
    public async Task PlanSwitchRepricesWithThePeriodAmountOfTheTargetPlan()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "BF2026", AmountType = "percent", Amount = 10, PeriodAmounts = new() { [4] = 25 } });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.DiscountCode] = "BF2026";
        checkoutMetadata[Keys.PriceCents] = "727";
        remote = Snapshot(priceCents: 727);
        var service = Service(stripe.Object);
        var requests = new List<StripePriceChangeRequest>();
        OnPriceChange(_ => new StripePriceChange(remote = Snapshot(plan: "l_prem_plus", priceCents: 2677), "in_up", "paid"), requests);
        await service.StripeInvoicePaid(Invoice(total: 727));

        await service.ChangePlan("buyer", "sub_1", "l_prem_plus");

        Assert.That(requests.Single().UnitAmount, Is.EqualTo(2677), "35.69 minus the 25% of the 4 week period");
    }

    [Test]
    public async Task RenewalExtendsTheSameAccessByOnePeriod()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle"));
        var access = await Access("sub_1");
        Assert.That(access.Expires, Is.EqualTo(DateTime.UtcNow.AddSeconds(2 * FourWeeks)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(await db.OwnerShips.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(968, "eur")]
    [TestCase(969, "usd")]
    public async Task DirectInvoiceWithAnotherPriceIsMarkedForRefundNotDropped(long total, string currency)
    {
        var outcome = await Service(stripe.Object).StripeInvoicePaid(Invoice(total: total, currency: currency));
        Assert.That(outcome, Is.Null);
        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
        Assert.That((await RefundMark("stripe", "in_1")).ToString(), Is.EqualTo(Refund));
    }

    [Test]
    public async Task ManagedPaymentsInvoiceInLocalCurrencyGrantsExactlyOnce()
    {
        remote = Snapshot(managed: true);
        var service = Service(stripe.Object);
        // Adaptive Pricing: the buyer paid in BRL, which must not be compared to the EUR price.
        var invoice = Invoice(total: 5890, currency: "brl", country: "BR");
        var outcome = await service.StripeInvoicePaid(invoice);
        await service.StripeInvoicePaid(invoice);

        Assert.That(outcome.TaxRemittedByProcessor, Is.True);
        Assert.That(outcome.Currency, Is.EqualTo("BRL"));
        Assert.That(outcome.EurAmount, Is.EqualTo(9.69m));
        Assert.That((await Subscription("sub_1")).ManagedPayments, Is.True);
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
    }

    [Test]
    public async Task InvoiceForAPlanThatIsNoValidSwitchIsMarkedForRefund()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "not_a_plan");
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle")), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_2"), Is.Zero);
        Assert.That((await RefundMark("stripe", "in_2")).ToString(), Is.EqualTo(Refund));
    }

    /// <summary>
    /// Regression: Stripe applied a plan switch whose local update was lost (e.g. an upgrade paid later); the renewal
    /// at the new price was rejected and only logged, so the paid period was never granted.
    /// </summary>
    [Test]
    public async Task RenewalAfterALostLocalPlanSwitchFollowsStripeAndGrants()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(plan: "l_prem_plus", priceCents: 3569);

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 3569)), Is.Not.Null);

        var subscription = await Subscription("sub_1");
        Assert.That(subscription.Product.Slug, Is.EqualTo("l_prem_plus"));
        Assert.That(subscription.PriceCents, Is.EqualTo(3569));
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_prem_plus"));
        Assert.That(await RefundMark("stripe", "in_2"), Is.Null);
    }

    [Test]
    public async Task DirectSaleToABlockedCountryIsCancelledAndMarkedForRefund()
    {
        stripe.Setup(s => s.CancelNow("sub_1")).ReturnsAsync(() => remote = Snapshot("canceled"));
        var outcome = await Service(stripe.Object).StripeInvoicePaid(Invoice(country: "CH"));
        Assert.That(outcome, Is.Null);
        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        Assert.That((await RefundMark("stripe", "in_1")).ToString(), Is.EqualTo(Refund));
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("expired"));
    }

    [Test]
    public async Task CancelAndResumeUseCancelAtPeriodEnd()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = Snapshot(cancelAtPeriodEnd: true));
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = Snapshot());

        await service.CancelSubscription("buyer", "sub_1");
        var subscription = await Subscription("sub_1");
        Assert.That(subscription.Status, Is.EqualTo("cancelled"));
        Assert.That(subscription.EndsAt, Is.EqualTo(periodEnd));
        Assert.That((await service.GetUserSubscriptions("buyer")).Single().CanResume, Is.True);

        Assert.That(await service.ResumeSubscription("buyer", "sub_1"), Is.True);
        Assert.That(subscription.Status, Is.EqualTo("active"));
        Assert.That(subscription.EndsAt, Is.Null);
    }

    /// <summary>
    /// Regression: an end date set with cancel_at (dashboard/support) was shown as resumable,
    /// but clearing cancel_at_period_end does not undo it, so Resume always failed.
    /// </summary>
    [Test]
    public async Task EndDateSetBySupportIsNotResumable()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(cancelAt: periodEnd.AddDays(-10));
        await service.StripeSubscriptionChanged("sub_1");

        var listed = (await service.GetUserSubscriptions("buyer")).Single();
        Assert.That(listed.Status, Is.EqualTo("cancelled"));
        Assert.That(listed.EndsAt, Is.EqualTo(periodEnd.AddDays(-10)));
        Assert.That(listed.CanResume, Is.False);
        Assert.ThrowsAsync<ApiException>(() => service.ResumeSubscription("buyer", "sub_1"));
        stripe.Verify(s => s.SetCancelAtPeriodEnd(It.IsAny<string>(), false), Times.Never);
    }

    [Test]
    public async Task SubscriptionListExposesProviderCapabilities()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        db.Subscriptions.Add(new UserSubscription { User = await Buyer(), Product = premium, ExternalId = "123", Status = "active" });
        await db.SaveChangesAsync();

        var list = (await service.GetUserSubscriptions("buyer")).ToList();
        var stripeRow = list.Single(s => s.ExternalId == "sub_1");
        var legacy = list.Single(s => s.ExternalId == "123");
        Assert.That(stripeRow.ProviderSlug, Is.EqualTo("stripe"));
        Assert.That(stripeRow.CanResume && stripeRow.CanChangePlan, Is.True);
        Assert.That(legacy.ProviderSlug, Is.EqualTo("lemonsqueezy"), "rows without a provider are Lemon Squeezy subscriptions");
        Assert.That(legacy.CanResume && legacy.CanChangePlan, Is.True);
    }

    [Test]
    public async Task UpgradePaidAtOnceIsAppliedAndItsProrationIsRecordedWithoutAnExtraPeriod()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        var requests = new List<StripePriceChangeRequest>();
        OnPriceChange(_ => new StripePriceChange(remote = Snapshot(plan: "l_prem_plus", priceCents: 3569), "in_up", "paid"), requests);

        var result = await service.ChangePlan("buyer", "sub_1", "l_prem_plus");

        Assert.That(result.Status, Is.EqualTo("completed"));
        var change = requests.Single();
        Assert.That(change.InvoiceNow, Is.True);
        Assert.That(change.UnitAmount, Is.EqualTo(3569));
        Assert.That(change.IntervalWeeks, Is.EqualTo(4));
        Assert.That(change.PlanSlug, Is.EqualTo("l_prem_plus"));
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_prem_plus"));
        Assert.That((await Subscription("sub_1")).PriceCents, Is.EqualTo(3569));

        // The proration webhook arrives later: revenue to record, but no new period and no second plan change.
        var proration = await service.StripeInvoicePaid(Invoice("in_up", "subscription_update", 2000));
        Assert.That(proration.IsProration, Is.True);
        Assert.That(proration.Amount, Is.EqualTo(20m));
        Assert.That(await Grants("stripe-invoice-in_up"), Is.Zero);
        Assert.That(await db.SubscriptionPlanChanges.CountAsync(c => c.InvoiceId == "in_up"), Is.EqualTo(1));
    }

    /// <summary>
    /// Regression: error_if_incomplete applied an upgrade whose payment was still processing; now the upgrade is a
    /// pending update that applies (and is recorded) once its proration invoice is paid.
    /// </summary>
    [Test]
    public async Task DelayedUpgradePaymentStaysPendingUntilItsInvoiceIsPaid()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        OnPriceChange(_ => new StripePriceChange(remote = Snapshot(pending: true), "in_up", "open"));

        Assert.That((await service.ChangePlan("buyer", "sub_1", "l_prem_plus")).Status, Is.EqualTo("pending"));
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_premium"));
        Assert.ThrowsAsync<ApiException>(() => service.StripeInvoicePaid(Invoice("in_up", "subscription_update", 2000)),
            "paid but not yet applied by Stripe: retried");

        remote = Snapshot(plan: "l_prem_plus", priceCents: 3569);
        var proration = await service.StripeInvoicePaid(Invoice("in_up", "subscription_update", 2000));
        Assert.That(proration, Is.Not.Null);
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_prem_plus"));
        Assert.That(await db.SubscriptionPlanChanges.CountAsync(c => c.InvoiceId == "in_up"), Is.EqualTo(1));
        Assert.That((await Subscription("sub_1")).PriceCents, Is.EqualTo(3569));
    }

    [Test]
    public async Task DowngradeChangesTheNextBillWithoutProration()
    {
        checkoutMetadata[Keys.ProductSlug] = "l_prem_plus";
        checkoutMetadata[Keys.PriceCents] = "3569";
        remote = Snapshot(priceCents: 3569);
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice(total: 3569));
        var requests = new List<StripePriceChangeRequest>();
        OnPriceChange(_ => new StripePriceChange(remote = Snapshot(plan: "l_premium"), null, null), requests);

        Assert.That((await service.ChangePlan("buyer", "sub_1", "l_premium")).Status, Is.EqualTo("completed"));
        Assert.That(requests.Single().InvoiceNow, Is.False);
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_prem_plus"), "the paid tier lasts until renewal");
        Assert.That((await Subscription("sub_1")).Product.Slug, Is.EqualTo("l_premium"));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle")), Is.Not.Null);
        Assert.That((await Access("sub_1")).Product.Slug, Is.EqualTo("l_premium"));
    }

    /// <summary>
    /// Regression: the session's recurring price was the discounted first price and was raised after the first invoice,
    /// so Stripe's page showed the discount as the renewal price. Now the recurring price is the full price from the start
    /// (a one-time coupon lowers the first invoice), nothing is repriced and only the discounted first total is accepted.
    /// </summary>
    [Test]
    public async Task FirstPaymentOnlyDiscountNeedsNoRepriceAfterTheFirstInvoice()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.DiscountCode] = "INTRO";
        checkoutMetadata[Keys.FirstPriceCents] = "485";
        remote = Snapshot();
        var service = Service(stripe.Object);

        Assert.That(await service.StripeInvoicePaid(Invoice(total: 485)), Is.Not.Null);

        stripe.Verify(s => s.ChangePrice(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripePriceChangeRequest>()), Times.Never);
        Assert.That((await Subscription("sub_1")).PriceCents, Is.EqualTo(969));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle")), Is.Not.Null);
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));
    }

    [TestCase(969, Description = "the coupon was not applied")]
    [TestCase(500, Description = "another discount")]
    public async Task FirstInvoiceOfAFirstPaymentCodeMustChargeExactlyTheDiscountedPrice(long total)
    {
        checkoutMetadata[Keys.FirstPriceCents] = "485";
        remote = Snapshot();
        var service = Service(stripe.Object);

        Assert.That(await service.StripeInvoicePaid(Invoice(total: total)), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
        Assert.That(await RefundMark("stripe", "in_1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That((await db.PaymentRequests.SingleAsync(r => r.SessionId == "in_1")).Country, Is.EqualTo("US"),
            "the revenue export lists the sale under its checkout country");
    }

    [Test]
    public async Task RenewalAfterAFirstPaymentCodeMustChargeTheFullPrice()
    {
        checkoutMetadata[Keys.FirstPriceCents] = "485";
        remote = Snapshot();
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice(total: 485));

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 485)), Is.Null, "the coupon was once only");
        Assert.That(await service.StripeInvoicePaid(Invoice("in_3", "subscription_cycle", 969)), Is.Not.Null);
    }

    [Test]
    public async Task FullRefundRevokesTheInvoicePeriodOnce()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        var invoice = Invoice();
        await service.StripeInvoiceRefunded(invoice, 969, 969);
        await service.StripeInvoiceRefunded(invoice, 969, 969);
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.FiniteTransactions.CountAsync(t => t.Reference.StartsWith("revert transaction")), Is.EqualTo(2));
        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
        // A redelivered invoice.paid after the refund must not grant the period again.
        Assert.That(await service.StripeInvoicePaid(invoice), Is.Null);
    }

    [Test]
    public async Task PartialRefundKeepsAccess()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        await service.StripeInvoiceRefunded(Invoice(), 969, 100);
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(), Is.Zero);
        Assert.That((await Access("sub_1")).Expires, Is.GreaterThan(DateTime.UtcNow.AddDays(20)));
    }

    /// <summary>
    /// Regression: a self-service refund of all but one cent was a partial refund, which keeps the paid period.
    /// </summary>
    [Test]
    public async Task SelfServiceRefundIsAlwaysTheFullRemainingAmount()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        stripe.Setup(s => s.ListInvoices("sub_1")).ReturnsAsync(new List<SubscriptionInvoice>
        {
            new() { Id = "in_1", Status = "paid", Total = 969, BillingReason = "initial", CreatedAt = DateTime.UtcNow }
        });
        stripe.Setup(s => s.RefundInvoice("in_1", null))
            .ReturnsAsync(new RefundResponse { Id = "in_1", Refunded = true, RefundedAmount = 969, Status = "refunded" });

        Assert.ThrowsAsync<ApiException>(() => service.RefundSubscriptionPayment("buyer", "sub_1", "in_1", new RefundRequest { Amount = 968 }));
        stripe.Verify(s => s.RefundInvoice(It.IsAny<string>(), It.IsAny<long?>()), Times.Never);

        await service.RefundSubscriptionPayment("buyer", "sub_1", "in_1", null);
        Assert.That(await db.RefundedSubscriptionInvoices.CountAsync(i => i.InvoiceId == "in_1"), Is.EqualTo(1));
        Assert.That((await Access("sub_1")).Expires, Is.LessThan(DateTime.UtcNow.AddMinutes(1)));
    }

    [Test]
    public async Task OneTimeChargeRefundIsNotTreatedAsSubscriptionRefund()
    {
        var buyer = await users.GetOrCreate("buyer");
        db.FiniteTransactions.Add(new FiniteTransaction { User = buyer, Product = premium, Amount = 1, Reference = "pi_onetime" });
        await db.SaveChangesAsync();
        Assert.That(await Service(stripe.Object).StripeSubscriptionInvoiceForPayment("pi_onetime"), Is.Null);
        stripe.Verify(s => s.FindInvoiceByPaymentIntent(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task PaidInvoiceOfASubscriptionNotSoldHereIsMarkedForRefund()
    {
        checkoutMetadata.Clear();
        remote = Snapshot();
        var service = Service(stripe.Object);
        await service.StripeSubscriptionChanged("sub_1");
        Assert.That(await service.StripeInvoicePaid(Invoice()), Is.Null);
        Assert.That(await db.Subscriptions.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That((await RefundMark("stripe", "in_1")).ToString(), Is.EqualTo(Refund));
    }

    [Test]
    public async Task SubscriptionUpdatedWebhookSyncsCancellationFromStripe()
    {
        var service = Service(stripe.Object);
        await service.StripeInvoicePaid(Invoice());
        remote = Snapshot(cancelAtPeriodEnd: true);
        await service.StripeSubscriptionChanged("sub_1");
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("cancelled"));
        remote = Snapshot("past_due");
        await service.StripeSubscriptionChanged("sub_1");
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("past_due"));
    }

    /// <summary>
    /// Regression: AccessProduct took the first SERVICE product with the slot tier, which can be a product that is
    /// not a sellable plan (no price, disabled).
    /// </summary>
    [Test]
    public async Task AccessTierOfSlotSubscriptionsResolvesToASubscriptionPlan()
    {
        var decoy = new TopUpProduct { Slug = "premium_plus-slots-4-gift", Title = "gift", Price = 0, Cost = 1, CurrencyCode = "eur",
            OwnershipSeconds = FourWeeks, SlotCount = 4, SlotTier = "premium_plus", Type = Product.ProductType.SERVICE };
        var current = new TopUpProduct { Slug = "l_premium-slots-4", Title = "slots", Price = 29.69m, Cost = 7200, CurrencyCode = "eur",
            OwnershipSeconds = FourWeeks, SlotCount = 4, SlotTier = "premium", Type = Product.ProductType.SERVICE };
        var plusSlots = new TopUpProduct { Slug = "l_prem_plus-slots-4", Title = "plus slots", Price = 99.69m, Cost = 27000, CurrencyCode = "eur",
            OwnershipSeconds = FourWeeks, SlotCount = 4, SlotTier = "premium_plus", Type = Product.ProductType.SERVICE };
        db.TopUpProducts.AddRange(decoy, current, plusSlots);
        var buyer = await users.GetOrCreate("buyer");
        var subscription = new UserSubscription { User = buyer, Product = current, ExternalId = "sub_slots", ProviderSlug = "stripe" };
        db.Subscriptions.Add(subscription);
        db.TierSlots.Add(new TierSlot { UserId = buyer.Id, SubscriptionId = "sub_slots", Tier = "premium_plus", Expires = periodEnd });
        await db.SaveChangesAsync();

        var accessProduct = typeof(SubscriptionService).GetMethod("AccessProduct", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var access = await (Task<Product>)accessProduct.Invoke(Service(stripe.Object), [subscription])!;
        Assert.That(access.Slug, Is.EqualTo("l_prem_plus-slots-4"));
    }

    /// <summary>
    /// Regression: the one-time handler read session.PaymentIntentId, which subscription-mode sessions do not have.
    /// The webhook also verifies that Stripe.net 51.1 (API 2026-04-22.dahlia) accepts events of an endpoint
    /// still pinned to 2026-03-25.dahlia.
    /// </summary>
    [Test]
    public async Task SignedWebhooksRecordTheCheckoutAndGrantTheInvoiceOnce()
    {
        var request = await SeedCheckout("stripe", "cs_1", premium, 9.69m);
        var service = Service(stripe.Object);
        var session = """
            {"id":"cs_1","object":"checkout.session","mode":"subscription","subscription":"sub_1","client_reference_id":"buyer","payment_status":"paid",
             "metadata":{"userId":"buyer","productSlug":"l_premium"}}
            """;
        Assert.That(await PostStripe(service, "checkout.session.completed", session), Is.TypeOf<OkResult>());
        Assert.That(request.State, Is.EqualTo(PaymentRequest.Status.PAID));

        Assert.That(await PostStripe(service, "invoice.paid", InvoiceJson()), Is.TypeOf<OkResult>());
        Assert.That(await PostStripe(service, "invoice.paid", InvoiceJson()), Is.TypeOf<OkResult>());

        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That(publishedPayments, Has.Count.EqualTo(1));
        Assert.That(await PaymentRecords("stripe"), Is.EqualTo(1));
        var record = await db.PaymentRecords.SingleAsync();
        Assert.That(record.TaxRemittedByProcessor, Is.False);
        Assert.That(record.Country, Is.EqualTo("US"), "a direct sale is recorded under its checkout (VAT) country, not the billing address");
        Assert.That(record.ExternalOrderId, Is.EqualTo("in_1"));
    }

    /// <summary>
    /// Regression: when publishing the payment failed after the grant, the retried webhook found the period granted
    /// and recorded nothing, so the PaymentRecord, event and creator revenue were lost.
    /// </summary>
    [Test]
    public async Task PaymentIsRecordedWhenTheWebhookIsRetriedAfterAPublishFailure()
    {
        var service = Service(stripe.Object);
        failingPublishes = 1;
        Assert.That(await PostStripe(service, "invoice.paid", InvoiceJson()), Is.InstanceOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(400));
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That(await PaymentRecords("stripe"), Is.Zero);

        Assert.That(await PostStripe(service, "invoice.paid", InvoiceJson()), Is.TypeOf<OkResult>());
        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That(await PaymentRecords("stripe"), Is.EqualTo(1));
        Assert.That(publishedPayments, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Regression: proration invoices were never recorded, so upgrade revenue was missing from the tax report.
    /// </summary>
    [Test]
    public async Task UpgradeProrationIsRecordedForTaxReporting()
    {
        var service = Service(stripe.Object);
        await PostStripe(service, "invoice.paid", InvoiceJson());
        OnPriceChange(_ => new StripePriceChange(remote = Snapshot(plan: "l_prem_plus", priceCents: 3569), "in_up", "paid"));
        await service.ChangePlan("buyer", "sub_1", "l_prem_plus");

        Assert.That(await PostStripe(service, "invoice.paid", InvoiceJson("in_up", "subscription_update", 2000)), Is.TypeOf<OkResult>());

        var proration = await db.PaymentRecords.SingleAsync(r => r.ExternalOrderId == "in_up");
        Assert.That(proration.GrossAmount, Is.EqualTo(20m));
        Assert.That(proration.CoinAmount, Is.Zero);
        Assert.That(proration.IsSubscriptionPayment, Is.True);
        Assert.That(await Grants("stripe-invoice-in_up"), Is.Zero);
    }

    /// <summary>
    /// A direct sale charges net + VAT: the record splits the VAT of the checkout country off the paid gross
    /// (9.69 net + 19% = 11.53 gross, VAT 1.84) and the creator share is based on the net price.
    /// </summary>
    [Test]
    public async Task DirectGrossPaymentIsRecordedWithItsVatSplitOff()
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0,
            RevenueSharePercent = 10, IsActive = true });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.Country] = "DE";
        checkoutMetadata[Keys.CreatorCode] = "CREATOR";
        checkoutMetadata[Keys.PriceCents] = "1153";
        remote = Snapshot(priceCents: 1153);

        await PostStripe(Service(stripe.Object), "invoice.paid", InvoiceJson(total: 1153));

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.GrossAmount, record.TaxAmount, record.Subtotal, record.NetAmount), Is.EqualTo((11.53m, 1.84m, 9.69m, 9.69m)));
        Assert.That((record.TaxRate, record.TaxName, record.TaxRemittedByProcessor), Is.EqualTo((19.0, "VAT", false)));
        Assert.That((await db.CreatorCodeRevenues.SingleAsync()).FinalPrice, Is.EqualTo(9.69m));
    }

    /// <summary>
    /// A plan switch reprices to the target plan's net price plus the VAT of the checkout country (35.69 * 1.19 = 42.47);
    /// a "first payment only" code does not apply to it.
    /// </summary>
    [Test]
    public async Task PlanSwitchAfterAFirstPaymentCodeUsesTheGrossPriceWithoutIt()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.Country] = "DE";
        checkoutMetadata[Keys.DiscountCode] = "INTRO";
        checkoutMetadata[Keys.PriceCents] = "1153";
        checkoutMetadata[Keys.FirstPriceCents] = "576";
        remote = Snapshot(priceCents: 1153);
        var service = Service(stripe.Object);
        var requests = new List<StripePriceChangeRequest>();
        OnPriceChange(r => new StripePriceChange(remote = Snapshot(plan: r.PlanSlug, priceCents: r.UnitAmount), "in_up", "paid"), requests);

        await service.StripeInvoicePaid(Invoice(total: 576));
        await service.ChangePlan("buyer", "sub_1", "l_prem_plus");

        Assert.That(requests.Single().UnitAmount, Is.EqualTo(4247));
        Assert.That((await Subscription("sub_1")).PriceCents, Is.EqualTo(4247));
    }

    /// <summary>
    /// Regression: the record named the invoice's billing country while the VAT in it is the checkout country's,
    /// so German VAT was reported under Austria (tax exports group by country).
    /// </summary>
    [Test]
    public async Task DirectSaleIsRecordedUnderTheCountryWhoseVatItContains()
    {
        checkoutMetadata[Keys.Country] = "DE";
        checkoutMetadata[Keys.PriceCents] = "1153";
        remote = Snapshot(priceCents: 1153);

        await PostStripe(Service(stripe.Object), "invoice.paid", InvoiceJson(total: 1153, country: "AT"));

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.Country, record.TaxAmount, record.TaxRate), Is.EqualTo(("DE", 1.84m, 19.0)));
        Assert.That(publishedPayments.Single().Address.CountryCode, Is.EqualTo("DE"));
    }

    /// <summary>
    /// Regression: a redelivered first invoice (its recording failed) took the creator base from the agreed (recurring)
    /// price: the share of 9.69 instead of the paid 4.84.
    /// </summary>
    [Test]
    public async Task RetriedFirstInvoiceKeepsTheCreatorShareOfWhatWasPaid()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0,
            RevenueSharePercent = 10, IsActive = true });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.Country] = "DE";
        checkoutMetadata[Keys.DiscountCode] = "INTRO";
        checkoutMetadata[Keys.CreatorCode] = "CREATOR";
        checkoutMetadata[Keys.PriceCents] = "1153";
        checkoutMetadata[Keys.FirstPriceCents] = "576";
        remote = Snapshot(priceCents: 1153);
        var service = Service(stripe.Object);
        failingPublishes = 1;

        await PostStripe(service, "invoice.paid", InvoiceJson(total: 576));
        Assert.That((await Subscription("sub_1")).PriceCents, Is.EqualTo(1153), "renewals are agreed at the full price");
        await PostStripe(service, "invoice.paid", InvoiceJson(total: 576));

        Assert.That((await db.CreatorCodeRevenues.SingleAsync()).FinalPrice, Is.EqualTo(4.84m));
        Assert.That((await db.PaymentRecords.SingleAsync()).NetAmount, Is.EqualTo(4.84m));
    }

    [Test]
    public async Task DirectInvoiceThatLacksTheVatIsRejected()
    {
        checkoutMetadata[Keys.Country] = "DE";
        checkoutMetadata[Keys.PriceCents] = "1153";
        remote = Snapshot(priceCents: 1153);
        var service = Service(stripe.Object);

        Assert.That(await service.StripeInvoicePaid(Invoice(total: 969)), Is.Null, "net only: marked for refund, not granted");
        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
    }

    /// <summary>
    /// Regression: the creator share of a Managed Payments payment subtracted a local-currency amount from the EUR price.
    /// </summary>
    [Test]
    public async Task ManagedPaymentsCreatorRevenueUsesTheAgreedEurPrice()
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 10,
            RevenueSharePercent = 5, IsActive = true });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.CreatorCode] = "CREATOR";
        checkoutMetadata[Keys.PriceCents] = "872";
        remote = Snapshot(managed: true, priceCents: 872);
        var service = Service(stripe.Object);

        await PostStripe(service, "invoice.paid", InvoiceJson(total: 5890, currency: "brl", country: null));
        await PostStripe(service, "invoice.paid", InvoiceJson(total: 5890, currency: "brl", country: null));

        var revenue = await db.CreatorCodeRevenues.SingleAsync();
        Assert.That(revenue.OriginalPrice, Is.EqualTo(9.69m));
        Assert.That(revenue.FinalPrice, Is.EqualTo(8.72m));
        Assert.That(revenue.DiscountAmount, Is.EqualTo(0.97m));
        Assert.That(revenue.CreatorRevenue, Is.EqualTo(0.436m));
        Assert.That(revenue.Currency, Is.EqualTo("EUR"));
        var record = await db.PaymentRecords.SingleAsync();
        Assert.That(record.TaxRemittedByProcessor, Is.True);
        Assert.That(record.Currency, Is.EqualTo("BRL"));
    }

    private async Task SeedCheckoutRequest(string ip)
    {
        var request = await SeedCheckout("stripe", "cs_1", premium, 11.53m);
        request.CreateOnIp = System.Net.IPAddress.Parse(ip);
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.PaymentRequestId] = request.Id.ToString();
    }

    private void DirectCheckoutIn(string country, string cardCountry)
    {
        checkoutMetadata[Keys.Country] = country;
        checkoutMetadata[Keys.PriceCents] = "1153";
        remote = Snapshot(priceCents: 1153);
        stripe.Setup(s => s.GetCardCountry("in_1")).ReturnsAsync(cardCountry);
        stripe.Setup(s => s.CancelNow("sub_1")).ReturnsAsync(() => remote = Snapshot("canceled", priceCents: 1153));
    }

    /// <summary>
    /// Regression: a buyer who checked out as German (VAT 19%) but whose billing address and card are both Austrian
    /// was granted with the German VAT. Both signals agreeing on another country rejects the sale like an unsellable one.
    /// </summary>
    [Test]
    public async Task BillingAddressAndCardInAnotherCountryThanTheCheckoutRejectTheSale()
    {
        DirectCheckoutIn("DE", "AT");

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 1153, country: "AT")), Is.Null);

        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
        Assert.That(await RefundMark("stripe", "in_1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
    }

    [TestCase("FR", Description = "the card is from a third country: no agreement, no proof")]
    [TestCase("DE", Description = "the card matches the checkout")]
    [TestCase(null, Description = "a payment method without a card country")]
    public async Task OneSignalAloneDoesNotRejectTheSale(string cardCountry)
    {
        DirectCheckoutIn("DE", cardCountry);

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 1153, country: "AT")), Is.Not.Null);
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task BillingCardAndCheckoutCountriesAndIpAreKeptWithThePayment()
    {
        await SeedCheckoutRequest("203.0.113.7");
        DirectCheckoutIn("DE", "FR");

        await PostStripe(Service(stripe.Object), "invoice.paid", InvoiceJson(total: 1153, country: "AT"));

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.CheckoutCountry, record.BillingCountry, record.PaymentMethodCountry, record.IpAddress),
            Is.EqualTo(("DE", "AT", "FR", "203.0.113.7")));
    }

    /// <summary>
    /// The checkout state (from the edge) is kept on the subscription (the per-state US cap counts by it) and with the payment;
    /// the billing address state is only evidence: a mismatch does not reject the sale.
    /// </summary>
    [Test]
    public async Task CheckoutStateAndBillingStateAreKeptAsEvidenceWithoutRejecting()
    {
        await SeedCheckoutRequest("203.0.113.7");
        DirectCheckoutIn("DE", "DE");
        checkoutMetadata[Keys.Region] = "ca";
        remote = Snapshot(priceCents: 1153);

        await PostStripe(Service(stripe.Object), "invoice.paid", InvoiceJson(total: 1153, country: "DE", state: "nv"));

        var record = await db.PaymentRecords.SingleAsync();
        Assert.That((record.CheckoutRegion, record.BillingRegion), Is.EqualTo(("CA", "NV")));
        Assert.That((await Subscription("sub_1")).CheckoutRegion, Is.EqualTo("CA"));
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Regression: a sale marked for a manual refund (no PaymentRecord) kept its checkout country but not its state, so the
    /// revenue export listed it with an empty CheckoutRegion.
    /// </summary>
    [Test]
    public async Task AManualRefundKeepsTheCheckoutState()
    {
        checkoutMetadata[Keys.FirstPriceCents] = "485";
        checkoutMetadata[Keys.Region] = "CA";
        remote = Snapshot();

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 969)), Is.Null);

        var request = await db.PaymentRequests.SingleAsync(r => r.SessionId == "in_1");
        Assert.That((request.State, request.Country, request.CheckoutRegion), Is.EqualTo((PaymentRequest.Status.REFUND_PENDING, "US", "CA")));
    }

    /// <summary>
    /// TR (tax-blocked for sales by Coflnet) is sold through Managed Payments, where Stripe is the seller: its first invoice is
    /// granted, not rejected like a direct sale to a blocked country.
    /// </summary>
    [Test]
    public async Task ManagedPaymentsFirstInvoiceFromATaxBlockedCountryIsGranted()
    {
        remote = Snapshot(managed: true, priceCents: 969);

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 5890, currency: "try", country: "TR")), Is.Not.Null);

        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        stripe.Verify(s => s.CancelNow(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ManagedPaymentsDoesNotLookUpTheCard()
    {
        remote = Snapshot(managed: true, priceCents: 969);

        Assert.That(await Service(stripe.Object).StripeInvoicePaid(Invoice(total: 5890, currency: "brl")), Is.Not.Null);
        stripe.Verify(s => s.GetCardCountry(It.IsAny<string>()), Times.Never);
    }

    private async Task RefundScenario()
    {
        db.CreatorCodes.Add(new CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0,
            RevenueSharePercent = 10, IsActive = true });
        await db.SaveChangesAsync();
        checkoutMetadata[Keys.CreatorCode] = "CREATOR";
        remote = Snapshot();
        var service = Service(stripe.Object);
        stripe.Setup(s => s.FindInvoiceByPaymentIntent("pi_1")).ReturnsAsync(Invoice());
        await PostStripe(service, "invoice.paid", InvoiceJson());
    }

    private Task PostChargeRefund(long refunded) => PostStripe(Service(stripe.Object), "charge.refunded",
        $$"""{"id":"ch_1","object":"charge","payment_intent":"pi_1","amount":969,"amount_refunded":{{refunded}},"currency":"eur"}""");

    private Task<List<decimal>> Reversals() => db.CreatorCodeRevenues.Where(r => r.CreatorRevenue < 0)
        .Select(r => r.CreatorRevenue).ToListAsync();

    /// <summary>
    /// Regression: the creator share was booked with the payment and never taken back, so refunded payments were still paid
    /// out. A full refund reverses all of it, a partial refund its proportion, and a redelivery changes nothing.
    /// </summary>
    [Test]
    public async Task FullRefundReversesTheCreatorShareOnce()
    {
        await RefundScenario();

        await PostChargeRefund(969);
        await PostChargeRefund(969);

        Assert.That(await Reversals(), Is.EqualTo(new[] { -0.969m }));
        Assert.That(await db.CreatorCodeRevenues.SumAsync(r => r.CreatorRevenue), Is.Zero);
    }

    [Test]
    public async Task PartialRefundsReverseTheShareProportionallyAndNeverMoreThanTheShare()
    {
        await RefundScenario();

        await PostChargeRefund(485);
        Assert.That(await Reversals(), Is.EqualTo(new[] { -0.485m }));
        await PostChargeRefund(485);
        Assert.That(await Reversals(), Is.EqualTo(new[] { -0.485m }), "a redelivery changes nothing");
        await PostChargeRefund(969);

        Assert.That(await Reversals(), Is.EqualTo(new[] { -0.485m, -0.484m }), "the growing cumulative refund adds its increase");
        Assert.That(await db.CreatorCodeRevenues.SumAsync(r => r.CreatorRevenue), Is.Zero);
        await PostChargeRefund(969);
        Assert.That(await db.CreatorCodeRevenues.CountAsync(), Is.EqualTo(3), "a redelivery changes nothing");
    }

    /// <summary>
    /// Regression: a growing Stripe refund rewrote the existing reversal row. Once that row was paid out (deducted from a
    /// payout), the further refund was hidden in a row already marked paid out, so the creator kept that part of the share.
    /// </summary>
    [Test]
    public async Task FurtherRefundAfterAPaidOutReversalIsANewUnpaidRow()
    {
        await RefundScenario();
        await PostChargeRefund(485);
        var paidOut = await db.CreatorCodeRevenues.SingleAsync(r => r.CreatorRevenue < 0);
        paidOut.IsPaidOut = true;
        await db.SaveChangesAsync();

        await PostChargeRefund(969);

        db.ChangeTracker.Clear();
        Assert.That((await db.CreatorCodeRevenues.SingleAsync(r => r.Id == paidOut.Id)).CreatorRevenue, Is.EqualTo(-0.485m));
        Assert.That(await db.CreatorCodeRevenues.Where(r => !r.IsPaidOut && r.CreatorRevenue < 0).Select(r => r.CreatorRevenue)
            .ToListAsync(), Is.EqualTo(new[] { -0.484m }));
    }

    private static string InvoiceJson(string id = "in_1", string reason = "subscription_create", long total = 969,
        string currency = "eur", string country = "DE", string state = null)
    {
        var stateJson = state == null ? "" : $$""","state":"{{state}}" """;
        var address = country == null ? "" : $$""","customer_address":{"country":"{{country}}","postal_code":"10115"{{stateJson}}}""";
        return $$$"""
            {"id":"{{{id}}}","object":"invoice","billing_reason":"{{{reason}}}","status":"paid","total":{{{total}}},"amount_paid":{{{total}}},
             "currency":"{{{currency}}}","created":1763380800{{{address}}},
             "parent":{"type":"subscription_details","subscription_details":{"subscription":"sub_1","metadata":{"userId":"buyer"} } } }
            """;
    }

    private async Task<IActionResult> PostStripe(SubscriptionService service, string type, string dataObject)
    {
        var json = $$$"""
            {"id":"evt_{{{Guid.NewGuid():N}}}","object":"event","api_version":"2026-03-25.dahlia","created":1763380800,
             "type":"{{{type}}}","livemode":false,"pending_webhooks":1,"data":{"object":{{{dataObject}}}}}
            """;
        var controller = Callback(service, new Dictionary<string, string> { ["STRIPE:SIGNING_SECRET"] = SigningSecret });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{json}"))).ToLowerInvariant();
        controller.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await controller.CreateStripe();
    }
}
