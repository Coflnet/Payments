using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Keys = Coflnet.Payments.Services.SubscriptionService.StripeMetadata;

namespace Coflnet.Payments.Services;

/// <summary>
/// Regression (test subscription 4891, its price changed in the Stripe dashboard): a renewal that charges another amount than the
/// agreed one is refused after the buyer paid. The change is detected when Stripe reports it, weeks before that renewal: a lower
/// price in the same terms becomes the agreed one, anything else is reported at once.
/// </summary>
public class StripeForeignPriceChangeTests : DirectSubscriptionTestBase
{
    private Mock<IStripeSubscriptionGateway> stripe;
    private StripeSubscriptionSnapshot remote;
    private DateTime periodEnd;
    private SubscriptionService service;

    [SetUp]
    public async Task SetUpPaidFourWeeks()
    {
        periodEnd = DateTime.UtcNow.AddDays(20);
        remote = Snapshot("l_premium", "price_1", 1153);
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.GetSubscription("sub_1")).ReturnsAsync(() => remote);
        service = Service(stripe.Object);
        Assert.That(await service.StripeInvoicePaid(Invoice("in_1", "subscription_create", 1153)), Is.Not.Null);
    }

    private StripeSubscriptionSnapshot Snapshot(string slug, string priceId, long cents, string interval = "week", long count = 4,
        string currency = "eur", string product = "prod_checkout") =>
        new("sub_1", "active", false, null, periodEnd, null, "cus_1", new Dictionary<string, string>
        {
            [Keys.UserId] = "buyer", [Keys.ProductSlug] = slug, [Keys.PriceCents] = cents.ToString(), [Keys.Mode] = "direct", [Keys.Country] = "DE"
        }, "si_1", priceId, product, cents, currency, null, false, DateTime.UtcNow, false, null, interval, count);

    private static StripeInvoiceInfo Invoice(string id, string reason, long total) =>
        new(id, "sub_1", reason, "paid", total, total, "eur", DateTime.UtcNow, "DE", "10115", "buyer@example.test", "Buyer",
            new Dictionary<string, string>());

    private async Task<long?> AgreedCents() => (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.ExternalId == "sub_1")).PriceCents;

    private static double Count(string kind) => PaymentMetrics.ForeignPriceChanges.WithLabels(kind).Value;

    /// <summary>A lower price set in the dashboard becomes the agreed price, so the next renewal is granted</summary>
    [Test]
    public async Task LowerPriceIsAcceptedAndTheRenewalGranted()
    {
        var before = Count("lower");
        remote = Snapshot("l_premium", "price_dashboard", 990);

        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(990));
        Assert.That(Count("lower"), Is.EqualTo(before + 1));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 990)), Is.Not.Null);
        Assert.That(await RefundMark("stripe", "in_2"), Is.Null);
        Assert.That(await Grants("stripe-invoice-in_2"), Is.EqualTo(1));

        await service.StripeSubscriptionChanged("sub_1");
        Assert.That(Count("lower"), Is.EqualTo(before + 1), "reported once per change");
    }

    /// <summary>A higher price is reported at once; the agreed price stays, so its renewal is still refused</summary>
    [Test]
    public async Task HigherPriceIsReportedAndNotAccepted()
    {
        var before = Count("higher");
        remote = Snapshot("l_premium", "price_dashboard", 1500);

        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(1153));
        Assert.That(Count("higher"), Is.EqualTo(before + 1));
        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 1500)), Is.Null);
        Assert.That(await RefundMark("stripe", "in_2"), Is.EqualTo(PaymentRequest.Status.REFUND_PENDING));
    }

    [TestCase("month", 1)]
    [TestCase("week", 1)]
    public async Task LowerPriceWithAnotherIntervalIsReportedAndNotAccepted(string interval, long count)
    {
        var before = Count("interval");
        remote = Snapshot("l_premium", "price_dashboard", 400, interval, count);

        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(1153));
        Assert.That(Count("interval"), Is.EqualTo(before + 1));
    }

    [Test]
    public async Task AnotherCurrencyIsReportedAndNotAccepted()
    {
        var before = Count("currency");
        remote = Snapshot("l_premium", "price_dashboard", 900, currency: "usd");

        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(1153));
        Assert.That(Count("currency"), Is.EqualTo(before + 1));
    }

    /// <summary>A plan switch made by this service records its price: Stripe's following update is not a foreign change</summary>
    [Test]
    public async Task OwnPlanChangeIsIgnored()
    {
        var plus = await Subscription("sub_1");
        plus.Product = premiumPlus;
        plus.PriceCents = 4247;
        await db.SaveChangesAsync();
        remote = Snapshot("l_prem_plus", "price_1", 4247);
        var downgraded = Snapshot("l_premium", "price_switched", 1153, product: StripePlans.ProductId("l_premium"));
        stripe.Setup(s => s.ChangePrice(It.IsAny<StripeSubscriptionSnapshot>(), It.IsAny<StripePriceChangeRequest>()))
            .ReturnsAsync(() => new StripePriceChange(remote = downgraded, null, null));
        var counts = (Count("lower"), Count("higher"), Count("interval"));

        Assert.That((await service.ChangePlan("buyer", "sub_1", "l_premium")).Status, Is.EqualTo("completed"));
        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(1153));
        Assert.That((Count("lower"), Count("higher"), Count("interval")), Is.EqualTo(counts));
    }

    /// <summary>The same price recreated with another id (e.g. a schedule phase with price_data) changes nothing</summary>
    [Test]
    public async Task SameTermsUnderAnotherPriceIdAreIgnored()
    {
        var counts = (Count("lower"), Count("higher"), Count("interval"), Count("currency"));
        remote = Snapshot("l_premium", "price_recreated", 1153);

        await service.StripeSubscriptionChanged("sub_1");

        Assert.That(await AgreedCents(), Is.EqualTo(1153));
        Assert.That((Count("lower"), Count("higher"), Count("interval"), Count("currency")), Is.EqualTo(counts));
    }

    /// <summary>A renewal paid before Stripe reported the dashboard change still finds the lower price accepted</summary>
    [Test]
    public async Task LowerPriceSeenFirstOnTheRenewalInvoiceIsGranted()
    {
        remote = Snapshot("l_premium", "price_dashboard", 990);

        Assert.That(await service.StripeInvoicePaid(Invoice("in_2", "subscription_cycle", 990)), Is.Not.Null);

        Assert.That(await RefundMark("stripe", "in_2"), Is.Null);
        Assert.That(await AgreedCents(), Is.EqualTo(990));
    }
}
