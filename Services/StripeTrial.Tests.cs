using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

/// <summary>Free trials of direct Stripe subscriptions (checkout: <see cref="SubscriptionTrials"/>)</summary>
public class StripeTrialTests : DirectSubscriptionTestBase
{
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private Dictionary<string, string> metadata;
    private TopUpProduct bazaarPro;
    private DateTime trialEnd;
    private PaymentRequest checkout;

    [SetUp]
    public async Task SetUpTrial()
    {
        bazaarPro = new TopUpProduct
        {
            Slug = "l_bazaarpro", Title = "Bazaar Pro", Price = 12.99m, Cost = 2100, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
            OwnershipSeconds = FourWeeks, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(bazaarPro);
        await db.SaveChangesAsync();
        checkout = await SeedCheckout("stripe", "cs_1", bazaarPro, 15.46m);
        db.ServicePerformanceDeclarations.Add(StripeExtensionTests.Evidence(checkout.Id, "Start my trial now", "de"));
        await db.SaveChangesAsync();
        trialEnd = DateTime.UtcNow.AddDays(3);
        metadata = new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = "l_bazaarpro", [Keys.PriceCents] = "1546", [Keys.Mode] = "direct",
            [Keys.Country] = "DE", [Keys.PaymentRequestId] = checkout.Id.ToString(), [SubscriptionTrials.MetadataKey] = "3"
        };
        remote = Snapshot("trialing", trialEnd);
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        stripe.Setup(s => s.GetCardCountry(It.IsAny<string>())).ReturnsAsync("DE");
    }

    private StripeSubscriptionSnapshot Snapshot(string status, DateTime periodEnd, bool cancelAtPeriodEnd = false) =>
        new("sub_1", status, cancelAtPeriodEnd, cancelAtPeriodEnd ? periodEnd : null, periodEnd, null, "cus_1",
            new Dictionary<string, string>(metadata), "si_1", "price_1", "prod_checkout", 1546, "eur", null, false, DateTime.UtcNow,
            TrialEnd: trialEnd);

    private static StripeInvoiceInfo Invoice(string id, string reason, long total, string country = "DE") =>
        new(id, "sub_1", reason, "paid", total, total, "eur", DateTime.UtcNow, country, "10115", "buyer@example.test", "Buyer",
            new Dictionary<string, string>());

    private static Stripe.Checkout.Session Session() => new()
    {
        Id = "cs_1", SubscriptionId = "sub_1", ClientReferenceId = "buyer", PaymentStatus = "no_payment_required",
        Metadata = new Dictionary<string, string> { [Keys.UserId] = "buyer" }
    };

    private Task Record(SubscriptionService service, SubscriptionPaymentOutcome outcome) =>
        StripeExtensionTests.RecordPayment(Callback(service), outcome, "stripe");

    /// <summary>
    /// The trial starts with checkout.session.completed: the subscription's own access runs until the trial end, the trial is recorded
    /// for the buyer and the confirmation names the trial end, the first charge and what follows; nothing is recorded as a payment
    /// </summary>
    [Test]
    public async Task CheckoutCompletedStartsTheTrialAndPublishesItsConfirmationOnce()
    {
        var service = Service(stripe.Object);

        await Record(service, await service.StripeCheckoutCompleted(Session()));
        await Record(service, await service.StripeInvoicePaid(Invoice("in_0", "subscription_create", 0)));
        await Record(service, await service.StripeCheckoutCompleted(Session()));

        var subscription = await Subscription("sub_1");
        Assert.That(subscription.Status, Is.EqualTo("on_trial"));
        Assert.That(subscription.TrialEndsAt, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(subscription.TrialUsedAt, Is.Not.Null, "the confirmation is marked published");
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await db.TrialUsages.SingleAsync(), Has.Property(nameof(TrialUsage.ProductId)).EqualTo(bazaarPro.Id)
            .And.Property(nameof(TrialUsage.ExternalSubscriptionId)).EqualTo("sub_1"));
        Assert.That((await db.PaymentRequests.SingleAsync()).State, Is.EqualTo(PaymentRequest.Status.PAID));
        Assert.That(await RefundMark("stripe", "in_0"), Is.Null, "the zero invoice is no payment to refund");
        Assert.That(await PaymentRecords("stripe"), Is.Zero, "no money moved: nothing for the VAT export");
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero, "no period is granted, the trial is no paid term");

        var confirmation = publishedPayments.Single();
        Assert.That(confirmation.ConfirmationType, Is.EqualTo(PaymentConfirmationTypes.SubscriptionTrialStarted));
        Assert.That(confirmation.PaymentProviderTransactionId, Is.EqualTo("trial-sub_1"));
        Assert.That(confirmation.PayedAmount, Is.Zero);
        Assert.That((confirmation.SubscriptionTrialDays, confirmation.SubscriptionFirstChargeAmount, confirmation.SubscriptionRenewalAmount),
            Is.EqualTo(((int?)3, (decimal?)15.46m, (decimal?)15.46m)));
        Assert.That((confirmation.SubscriptionBillingWeeks, confirmation.SubscriptionRenewalWeeks), Is.EqualTo(((int?)4, (int?)4)));
        Assert.That(confirmation.SubscriptionTrialEndsAt, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(confirmation.SubscriptionNextBillingAt, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That((confirmation.TaxTreatment, confirmation.RenewalTaxTreatment, confirmation.VatRateBasisPoints),
            Is.EqualTo((PaymentTaxTreatment.Included, PaymentTaxTreatment.Included, 1900)));
        Assert.That((confirmation.DeclarationText, confirmation.LegalLocale), Is.EqualTo(("Start my trial now", "de")));
        Assert.That(confirmation.ServiceEndsAtUtc, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(confirmation.SubscriptionIsRenewal, Is.False);
    }

    /// <summary>Stripe does not guarantee the order: the zero invoice can come first and starts the trial as well</summary>
    [Test]
    public async Task TheZeroInvoiceAloneStartsTheTrial()
    {
        var service = Service(stripe.Object);

        await Record(service, await service.StripeInvoicePaid(Invoice("in_0", "subscription_create", 0)));

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(publishedPayments.Single().Email, Is.EqualTo("buyer@example.test"));
        Assert.That(await RefundMark("stripe", "in_0"), Is.Null);
    }

    /// <summary>A failed publish is retried by the next delivery (it is only marked published afterwards)</summary>
    [Test]
    public async Task AFailedConfirmationIsPublishedByTheRetry()
    {
        var service = Service(stripe.Object);
        failingPublishes = 1;

        Assert.ThrowsAsync<InvalidOperationException>(async () => await Record(service, await service.StripeCheckoutCompleted(Session())));
        Assert.That((await Subscription("sub_1")).TrialUsedAt, Is.Null);
        await Record(service, await service.StripeInvoicePaid(Invoice("in_0", "subscription_create", 0)));

        Assert.That(publishedPayments.Single().ConfirmationType, Is.EqualTo(PaymentConfirmationTypes.SubscriptionTrialStarted));
        Assert.That(await db.TrialUsages.CountAsync(), Is.EqualTo(1));
    }

    /// <summary>
    /// The first invoice after the trial (billing reason subscription_cycle) is the first payment: it must charge the agreed price,
    /// gets the first payment's checks and declaration, and extends the access from the trial end
    /// </summary>
    [Test]
    public async Task TheFirstPaymentAfterTheTrialExtendsTheAccessAndIsConfirmedAsTheFirstPayment()
    {
        var service = Service(stripe.Object);
        await Record(service, await service.StripeCheckoutCompleted(Session()));
        remote = Snapshot("active", trialEnd.AddSeconds(FourWeeks));

        await Record(service, await service.StripeInvoicePaid(Invoice("in_1", "subscription_cycle", 1546)));

        Assert.That(await Grants("stripe-invoice-in_1"), Is.EqualTo(1));
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(trialEnd.AddSeconds(FourWeeks)).Within(TimeSpan.FromSeconds(5)));
        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("active"));
        stripe.Verify(s => s.GetCardCountry("in_1"), Times.Once, "the card is evidence of the first payment");
        var payment = publishedPayments.Last();
        Assert.That(payment.ConfirmationType, Is.Null);
        Assert.That((payment.PayedAmount, payment.SubscriptionIsRenewal, payment.SubscriptionFollowsTrial), Is.EqualTo((15.46, (bool?)false, (bool?)true)));
        Assert.That(payment.DeclarationText, Is.EqualTo("Start my trial now"));
        Assert.That(await PaymentRecords("stripe"), Is.EqualTo(1));

        await Record(service, await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 1546)));
        Assert.That(publishedPayments.Last().SubscriptionIsRenewal, Is.True);
        Assert.That(publishedPayments.Last().SubscriptionFollowsTrial, Is.Null);
    }

    [Test]
    public async Task TheFirstPaymentAfterTheTrialMustChargeTheAgreedPrice()
    {
        var service = Service(stripe.Object);
        await service.StripeCheckoutCompleted(Session());
        remote = Snapshot("active", trialEnd.AddSeconds(FourWeeks));

        Assert.That(await service.StripeInvoicePaid(Invoice("in_1", "subscription_cycle", 1299)), Is.Null);

        Assert.That(await RefundMark("stripe", "in_1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        Assert.That(await Grants("stripe-invoice-in_1"), Is.Zero);
    }

    /// <summary>The first payment after a trial gets the first payment's country check: a billing address and card elsewhere reject it</summary>
    [Test]
    public async Task TheFirstPaymentAfterTheTrialIsCheckedLikeAFirstPayment()
    {
        stripe.Setup(s => s.GetCardCountry("in_1")).ReturnsAsync("AT");
        stripe.Setup(s => s.CancelNow("sub_1")).ReturnsAsync(() => Snapshot("canceled", trialEnd));
        var service = Service(stripe.Object);
        await service.StripeCheckoutCompleted(Session());
        remote = Snapshot("active", trialEnd.AddSeconds(FourWeeks));

        Assert.That(await service.StripeInvoicePaid(Invoice("in_1", "subscription_cycle", 1546, "AT")), Is.Null);

        Assert.That(await RefundMark("stripe", "in_1"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
        stripe.Verify(s => s.CancelNow("sub_1"), Times.Once);
    }

    /// <summary>A trial cancelled before it ends just expires with the trial: nothing is charged, nothing refunded</summary>
    [Test]
    public async Task ATrialCancelledBeforeItEndsExpiresWithTheTrial()
    {
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = Snapshot("trialing", trialEnd, true));
        var service = Service(stripe.Object);
        await service.StripeCheckoutCompleted(Session());

        await service.CancelSubscription("buyer", "sub_1");

        var subscription = await Subscription("sub_1");
        Assert.That((subscription.Status, subscription.EndsAt), Is.EqualTo(("cancelled", (DateTime?)trialEnd)));
        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(trialEnd).Within(TimeSpan.FromSeconds(1)));
        Assert.That(await db.PaymentRequests.CountAsync(r => r.State == PaymentRequest.Status.REFUND_PENDING), Is.Zero);
    }

    [Test]
    public async Task ACancelledTrialCanBeResumed()
    {
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", true)).ReturnsAsync(() => remote = Snapshot("trialing", trialEnd, true));
        stripe.Setup(s => s.SetCancelAtPeriodEnd("sub_1", false)).ReturnsAsync(() => remote = Snapshot("trialing", trialEnd));
        var service = Service(stripe.Object);
        await service.StripeCheckoutCompleted(Session());
        await service.CancelSubscription("buyer", "sub_1");

        await service.ResumeSubscription("buyer", "sub_1");

        Assert.That((await Subscription("sub_1")).Status, Is.EqualTo("on_trial"));
    }

    /// <summary>The trial lasts as Stripe says, but never longer than the checkout gave (plus a day for a session opened late)</summary>
    [Test]
    public async Task TheTrialIsNeverLongerThanTheCheckoutGave()
    {
        trialEnd = DateTime.UtcNow.AddDays(30);
        remote = Snapshot("trialing", trialEnd);
        var service = Service(stripe.Object);

        await service.StripeCheckoutCompleted(Session());

        Assert.That((await Access("sub_1")).Expires, Is.EqualTo(DateTime.UtcNow.AddDays(4)).Within(TimeSpan.FromMinutes(1)));
    }

    /// <summary>A trialing subscription whose checkout gave no trial (e.g. one added in the dashboard) gets no access from it</summary>
    [Test]
    public async Task ATrialThisServiceDidNotGiveGrantsNothing()
    {
        metadata.Remove(SubscriptionTrials.MetadataKey);
        remote = Snapshot("trialing", trialEnd);
        var service = Service(stripe.Object);

        Assert.That(await service.StripeCheckoutCompleted(Session()), Is.Null);
        Assert.That(await service.StripeInvoicePaid(Invoice("in_0", "subscription_create", 0)), Is.Null);

        Assert.That(await Access("sub_1"), Is.Null);
        Assert.That(await RefundMark("stripe", "in_0"), Is.Null);
    }
}
