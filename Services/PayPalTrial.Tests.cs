using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

/// <summary>Free trials of PayPal subscriptions: a plan variant that starts with a free TRIAL cycle</summary>
public class PayPalTrialTests : DirectSubscriptionTestBase
{
    private Mock<IPayPalSubscriptionApi> paypal;
    private TopUpProduct bazaarPro;
    private PaymentRequest checkout;
    private DateTime trialEnd;
    private PayPalSubscription remote;
    private readonly List<PayPalPlanRequest> createdPlans = [];

    [SetUp]
    public async Task SetUpTrial()
    {
        createdPlans.Clear();
        bazaarPro = new TopUpProduct
        {
            Slug = "l_bazaarpro", Title = "Bazaar Pro", Price = 12.99m, Cost = 2100, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
            OwnershipSeconds = FourWeeks, Type = Product.ProductType.SERVICE
        };
        db.TopUpProducts.Add(bazaarPro);
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = "l_bazaarpro", BillingWeeks = 4, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-PLAIN" });
        db.PayPalPlans.Add(new PayPalPlan { ProductSlug = "l_bazaarpro+trial3d", BillingWeeks = 4, RenewalWeeks = 4, Currency = "EUR", PlanId = "P-TRIAL" });
        await db.SaveChangesAsync();
        checkout = await SeedCheckout("paypal", "I-SUB", bazaarPro, 15.46m);
        checkout.Country = "DE";
        db.ServicePerformanceDeclarations.Add(StripeExtensionTests.Evidence(checkout.Id, "Start my trial now", "en"));
        await db.SaveChangesAsync();
        trialEnd = new DateTime(DateTime.UtcNow.AddDays(3).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        remote = Remote("P-TRIAL");
        paypal = new Mock<IPayPalSubscriptionApi>();
        paypal.Setup(p => p.Get("I-SUB")).ReturnsAsync(() => remote);
        paypal.Setup(p => p.CreateCatalogProduct(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("PROD-1");
        paypal.Setup(p => p.CreatePlan(It.IsAny<PayPalPlanRequest>())).Callback<PayPalPlanRequest>(createdPlans.Add).ReturnsAsync("P-NEW");
    }

    private PayPalSubscription Remote(string planId, string status = "ACTIVE", PayPalLastPayment lastPayment = null) => new()
    {
        Id = "I-SUB", Status = status, PlanId = planId, CustomId = SubscriptionService.PayPalCustomId(bazaarPro.Id, "buyer"),
        CreateTime = DateTime.UtcNow.AddMinutes(-5).ToString("o"), StartTime = DateTime.UtcNow.ToString("o"),
        BillingInfo = new PayPalBillingInfo { NextBillingTime = trialEnd.ToString("o"), LastPayment = lastPayment }
    };

    private SubscriptionService PayPalService(IConfiguration config = null) =>
        Service(paypal: paypal.Object, paypalPlans: new PayPalPlanCatalog(db, paypal.Object, config));

    private async Task Record(SubscriptionService service, IEnumerable<SubscriptionPaymentOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
            await StripeExtensionTests.RecordPayment(Callback(service), outcome, "paypal");
    }

    private static PayPalSale Sale(string id, string total) => new()
    {
        Id = id, State = "completed", Amount = new PayPalSaleAmount { Total = total, Currency = "EUR" }, BillingAgreementId = "I-SUB",
        CreateTime = DateTime.UtcNow.ToString("o")
    };

    /// <summary>
    /// PayPal activates a trial plan's subscription without a sale: the access runs until PayPal's first billing time, the trial is
    /// recorded and confirmed once, nothing is recorded as a payment
    /// </summary>
    [Test]
    public async Task ActivationOfATrialPlanStartsTheTrialAndConfirmsItOnce()
    {
        var service = PayPalService();

        await Record(service, await service.PayPalSubscriptionChanged("I-SUB"));
        await Record(service, await service.PayPalSubscriptionChanged("I-SUB"));

        var subscription = await Subscription("I-SUB");
        Assert.That(subscription.Status, Is.EqualTo("on_trial"));
        Assert.That(subscription.TrialEndsAt, Is.EqualTo(trialEnd));
        Assert.That((await Access("I-SUB")).Expires, Is.EqualTo(trialEnd));
        Assert.That(await db.TrialUsages.CountAsync(t => t.ProductId == bazaarPro.Id && t.ExternalSubscriptionId == "I-SUB"), Is.EqualTo(1));
        Assert.That(await PaymentRecords("paypal"), Is.Zero);
        var confirmation = publishedPayments.Single();
        Assert.That(confirmation.ConfirmationType, Is.EqualTo(PaymentConfirmationTypes.SubscriptionTrialStarted));
        Assert.That((confirmation.PaymentProviderTransactionId, confirmation.PayedAmount), Is.EqualTo(("trial-I-SUB", 0.0)));
        Assert.That((confirmation.SubscriptionTrialDays, confirmation.SubscriptionFirstChargeAmount, confirmation.SubscriptionTrialEndsAt),
            Is.EqualTo(((int?)3, (decimal?)15.46m, (DateTime?)trialEnd)));
        Assert.That(confirmation.DeclarationText, Is.EqualTo("Start my trial now"));
    }

    /// <summary>The first sale at the trial end charges the first price, extends the access from the trial end and is the first payment</summary>
    [Test]
    public async Task TheFirstSaleAfterTheTrialIsTheFirstPayment()
    {
        var service = PayPalService();
        await Record(service, await service.PayPalSubscriptionChanged("I-SUB"));
        paypal.Setup(p => p.GetSale("SALE-1")).ReturnsAsync(Sale("SALE-1", "15.46"));

        await Record(service, [await service.PayPalSaleCompleted(Sale("SALE-1", "15.46"))]);

        Assert.That(await Grants("paypal-sale-SALE-1"), Is.EqualTo(1));
        Assert.That((await Access("I-SUB")).Expires, Is.EqualTo(trialEnd.AddSeconds(FourWeeks)).Within(TimeSpan.FromSeconds(5)));
        Assert.That((await Subscription("I-SUB")).Status, Is.EqualTo("active"));
        var payment = publishedPayments.Last();
        Assert.That((payment.PayedAmount, payment.SubscriptionIsRenewal, payment.SubscriptionFollowsTrial), Is.EqualTo((15.46, (bool?)false, (bool?)true)));
        Assert.That(payment.DeclarationText, Is.EqualTo("Start my trial now"));
        Assert.That(await PaymentRecords("paypal"), Is.EqualTo(1));
    }

    [Test]
    public async Task APlainPlanHasNoTrial()
    {
        remote = Remote("P-PLAIN", lastPayment: new PayPalLastPayment { Time = DateTime.UtcNow.ToString("o") });
        paypal.Setup(p => p.Transactions("I-SUB", It.IsAny<DateTime>(), It.IsAny<DateTime>())).ReturnsAsync([]);
        var service = PayPalService();

        var outcomes = await service.PayPalSubscriptionChanged("I-SUB");

        Assert.That(outcomes, Is.Empty);
        Assert.That((await Subscription("I-SUB")).Status, Is.EqualTo("active"));
        Assert.That(await Access("I-SUB"), Is.Null);
    }

    /// <summary>A trial cancelled before it ends just expires with the trial (PayPal cancels at once, nothing was charged)</summary>
    [Test]
    public async Task ATrialCancelledBeforeItEndsExpiresWithTheTrial()
    {
        var service = PayPalService();
        await service.PayPalSubscriptionChanged("I-SUB");

        await service.CancelSubscription("buyer", "I-SUB");

        var subscription = await Subscription("I-SUB");
        Assert.That((subscription.Status, subscription.EndsAt), Is.EqualTo(("cancelled", (DateTime?)trialEnd)));
        paypal.Verify(p => p.Cancel("I-SUB", It.IsAny<string>()), Times.Once);
    }

    /// <summary>The trial variant is its own plan: the plain plan is untouched and a release without trials never picks the variant</summary>
    [Test]
    public async Task TheTrialVariantIsAPlanOfItsOwn()
    {
        db.PayPalPlans.RemoveRange(db.PayPalPlans);
        await db.SaveChangesAsync();
        var catalog = new PayPalPlanCatalog(db, paypal.Object, null);

        Assert.That(await catalog.GetOrCreate(bazaarPro, trialDays: 5), Is.EqualTo("P-NEW"));

        var stored = await db.PayPalPlans.SingleAsync();
        Assert.That(stored.ProductSlug, Is.EqualTo("l_bazaarpro+trial5d"));
        Assert.That(createdPlans.Single().TrialDays, Is.EqualTo(5));
        Assert.That(await catalog.IsPlanOf("P-NEW", bazaarPro), Is.True);
        Assert.That(await catalog.TrialDays("P-NEW", bazaarPro), Is.EqualTo(5));
        paypal.Setup(p => p.CreatePlan(It.IsAny<PayPalPlanRequest>())).Callback<PayPalPlanRequest>(createdPlans.Add).ReturnsAsync("P-PLAIN2");
        Assert.That(await catalog.GetOrCreate(bazaarPro), Is.EqualTo("P-PLAIN2"), "the plain plan is another one");
        Assert.That(createdPlans.Last().TrialDays, Is.Null);
        Assert.That(await catalog.TrialDays("P-PLAIN2", bazaarPro), Is.Null);
    }

    /// <summary>A configured plan override (PAYPAL:SUBSCRIPTION_PLANS) has no trial cycle: a trial checkout uses a created variant</summary>
    [Test]
    public async Task AConfiguredPlanIsNotUsedForTrials()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["PAYPAL:SUBSCRIPTION_PLANS:l_bazaarpro"] = "P-CONFIGURED"
        }).Build();
        var catalog = new PayPalPlanCatalog(db, paypal.Object, config);

        Assert.That(await catalog.GetOrCreate(bazaarPro), Is.EqualTo("P-CONFIGURED"));
        Assert.That(await catalog.GetOrCreate(bazaarPro, trialDays: 3), Is.EqualTo("P-TRIAL"));
        Assert.That(await catalog.IsPlanOf("P-TRIAL", bazaarPro), Is.True);
        Assert.That(await catalog.IsPlanOf("P-PLAIN", bazaarPro), Is.False, "created plain plans stay replaced by the override");
    }

    [Test]
    public void ATrialPlanStartsWithAFreeTrialCycleOfDays()
    {
        var body = PayPalSubscriptionApi.PlanBody(new PayPalPlanRequest("PROD-1", "Bazaar Pro", "d", 13, 34.99m, "EUR", 4, 12.99m, 3));
        var cycles = (JArray)body["billing_cycles"];

        Assert.That(cycles.Select(c => ((string)c["tenure_type"], (int)c["sequence"], (int)c["total_cycles"],
                (string)c["frequency"]["interval_unit"], (int)c["frequency"]["interval_count"], (string)c["pricing_scheme"]["fixed_price"]["value"])),
            Is.EqualTo(new[]
            {
                ("TRIAL", 1, 1, "DAY", 3, "0.00"), ("TRIAL", 2, 1, "WEEK", 13, "34.99"), ("REGULAR", 3, 0, "WEEK", 4, "12.99")
            }));
    }

    [Test]
    public void ATrialSubscriptionPricesTheCyclesAfterTheTrial()
    {
        var body = PayPalSubscriptionApi.CreateBody(new PayPalSubscriptionRequest("P-TRIAL", "1;buyer", 15.46m, "EUR", "https://ok",
            "https://cancel", null, 15.46m, TrialDays: 3));
        var cycles = (JArray)body["plan"]["billing_cycles"];

        Assert.That(cycles.Select(c => ((int)c["sequence"], (string)c["pricing_scheme"]["fixed_price"]["value"])),
            Is.EqualTo(new[] { (1, "0.00"), (2, "15.46"), (3, "15.46") }));
    }

    [Test]
    public void PlansWithoutATrialKeepTheirTwoCycles()
    {
        var plan = PayPalSubscriptionApi.PlanBody(new PayPalPlanRequest("PROD-1", "Premium", "d", 4, 9.69m, "EUR"));
        var subscription = PayPalSubscriptionApi.CreateBody(new PayPalSubscriptionRequest("P-1", "1;buyer", 5.81m, "EUR", "https://ok",
            "https://cancel", null, 11.63m));

        Assert.That(((JArray)plan["billing_cycles"]).Select(c => (int)c["sequence"]), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(((JArray)subscription["plan"]["billing_cycles"]).Select(c => (int)c["sequence"]), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task TermSwitchesAreRefusedDuringThePayPalTrial()
    {
        var service = PayPalService();
        await service.PayPalSubscriptionChanged("I-SUB");
        var request = new SubscriptionTermSwitchRequest
        {
            Weeks = 52, Declaration = StripeExtensionTests.Declaration("Switch now"), ExpectedChargedNow = 100m
        };

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service.SwitchTerm("buyer", "I-SUB", request)).Message,
            Is.EqualTo(SubscriptionService.TrialActive));
        paypal.Verify(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()), Times.Never);
    }
}
