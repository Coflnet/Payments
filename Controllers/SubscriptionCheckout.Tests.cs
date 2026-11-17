using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Stripe.Checkout;

namespace Payments.Controllers;

public class SubscriptionCheckoutControllerTests : DirectSubscriptionTestBase
{
    private Mock<IStripeSubscriptionGateway> stripe;
    private Mock<IPayPalSubscriptionApi> paypal;
    private Mock<LemonSqueezyService> lemonSqueezy;
    private SessionCreateOptions created;
    private PayPalSubscriptionRequest paypalRequest;

    [SetUp]
    public void SetUpGateways()
    {
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()))
            .Callback<SessionCreateOptions>(o => created = o)
            .ReturnsAsync(new Session { Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1" });
        lemonSqueezy = new Mock<LemonSqueezyService>(new ConfigurationBuilder().Build(), NullLogger<LemonSqueezyService>.Instance,
            db, new VariantCacheService(NullLogger<VariantCacheService>.Instance));
        lemonSqueezy.Setup(l => l.ValidateDiscountCodeAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync((Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)null);
        lemonSqueezy.Setup(l => l.ValidateDiscountCodeAsync("LSCODE", It.IsAny<bool?>()))
            .ReturnsAsync(new Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount
                { Code = "LSCODE", Id = "77", AmountType = "percent", Amount = 10, IsValid = true });
        paypal = new Mock<IPayPalSubscriptionApi>();
        paypal.Setup(p => p.PlanIdFor("l_premium")).Returns("P-PREMIUM");
        paypal.Setup(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()))
            .Callback<PayPalSubscriptionRequest>(r => paypalRequest = r)
            .ReturnsAsync(new PayPalSubscription
            {
                Id = "I-SUB", Status = "APPROVAL_PENDING",
                Links = [new PayPalLink { Rel = "approve", Href = "https://www.paypal.com/approve" }]
            });
    }

    private TopUpController Controller(string ipCountry, Dictionary<string, string> settings = null)
    {
        var values = new Dictionary<string, string>
        {
            ["SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED"] = "true",
            ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "true",
            ["SUBSCRIPTIONS:PAYPAL_ENABLED"] = "true"
        };
        foreach (var setting in settings ?? new Dictionary<string, string>())
            values[setting.Key] = setting.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var ip = new Mock<IIpCountryLookup>();
        ip.Setup(l => l.GetCountry(It.IsAny<string>())).ReturnsAsync(ipCountry);
        return new TopUpController(NullLogger<TopUpController>.Instance, db, products, transactions, config, users,
            null, lemonSqueezy.Object, new CreatorCodeService(NullLogger<CreatorCodeService>.Instance, db), null, ip.Object,
            new DiscountCodeService(db, NullLogger<DiscountCodeService>.Instance), new SubscriptionRouting(config),
            stripe.Object, paypal.Object);
    }

    private static TopUpOptions Options(string country, string discount = null) => new()
    {
        Country = country, UserIp = "203.0.113.7", SuccessUrl = "https://ok", CancelUrl = "https://cancel", DiscountCode = discount
    };

    [Test]
    public async Task EuBuyerGetsADirectStripeSubscriptionAtTheVatInclusivePlanPrice()
    {
        var response = await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(response.DirectLink, Is.EqualTo("https://checkout.stripe.com/c/cs_1"));
        Assert.That(created.ManagedPayments, Is.Null);
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(969));
        Assert.That(created.LineItems.Single().PriceData.Recurring.IntervalCount, Is.EqualTo(4));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Provider, Is.EqualTo("stripe"));
        Assert.That(request.SessionId, Is.EqualTo("cs_1"));
        Assert.That((await Buyer()).Country, Is.EqualTo("DE"));
    }

    [Test]
    public void DirectStripeSubscriptionRequiresTheIpCountry()
    {
        var error = Assert.ThrowsAsync<ApiException>(() => Controller("US").StripeSubscribe("buyer", "l_premium", Options("DE")));
        Assert.That(error.Message, Does.Contain("does not match your IP country"));
    }

    [Test]
    public async Task BuyerOutsideTheDirectCountriesGetsManagedPayments()
    {
        await Controller("BR").StripeSubscribe("buyer", "l_premium", Options("BR"));
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        Assert.That(created.LineItems.Single().PriceData.ProductData.TaxCode, Is.EqualTo("txcd_10103000"));
        Assert.That(created.SubscriptionData.Metadata["mode"], Is.EqualTo("managed"));
    }

    [Test]
    public void ManagedPaymentsCanBeSwitchedOff()
    {
        var controller = Controller("BR", new() { ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "false" });
        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options("BR")));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    [Test]
    public async Task LocalDiscountCodeLowersTheRecurringPrice()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "SAVE10", AmountType = "percent", Amount = 10, SubscriptionOnly = true });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "save10"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(872));
        Assert.That(created.SubscriptionData.Metadata["priceCents"], Is.EqualTo("872"));
    }

    [Test]
    public void DirectSubscriptionDoesNotAcceptLemonSqueezyCodes()
    {
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "LSCODE")));
        lemonSqueezy.Verify(l => l.ValidateDiscountCodeAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    private static Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount Value(ActionResult<Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount> result) =>
        (Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)((OkObjectResult)result.Result).Value;

    /// <summary>
    /// Regression: one-time Stripe/PayPal/CoinGate checkouts only checked the (initially empty) local table, so every
    /// existing Lemon Squeezy code stopped working there while the validation endpoint still reported it valid.
    /// </summary>
    [TestCase("stripe")]
    [TestCase("paypal")]
    [TestCase("coingate")]
    [TestCase(null)]
    public async Task OneTimeCheckoutsStillAcceptLemonSqueezyCodes(string provider)
    {
        Assert.That(Value(await Controller("DE").ValidateDiscountCode("LSCODE", false, provider)).IsValid, Is.True);
    }

    [Test]
    public async Task ValidationEndpointAgreesWithTheSubscriptionCheckout()
    {
        var controller = Controller("DE");
        Assert.That(Value(await controller.ValidateDiscountCode("LSCODE", true, "stripe")).IsValid, Is.False);
        Assert.That(Value(await controller.ValidateDiscountCode("LSCODE", true, "lemonsqueezy")).IsValid, Is.True);
    }

    [Test]
    public async Task LocalCodeIsCheckedBeforeLemonSqueezy()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "LSCODE", AmountType = "percent", Amount = 20 });
        await db.SaveChangesAsync();
        Assert.That(Value(await Controller("DE").ValidateDiscountCode("lscode", false, "stripe")).Amount, Is.EqualTo(20));
        lemonSqueezy.Verify(l => l.ValidateDiscountCodeAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Test]
    public async Task DiscountCodeIsUsedUpAfterItsMaxRedemptions()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "ONCE", AmountType = "percent", Amount = 10, MaxRedemptions = 1 });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "ONCE"));
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("other", "l_premium", Options("DE", "ONCE")));
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.EqualTo(1));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Once);
    }

    [Test]
    public async Task FirstPaymentOnlyCodesAreNotSoldWithPayPal()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "INTRO")));
        paypal.Verify(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()), Times.Never);
    }

    [Test]
    public void OneTimeTopUpProductIsNotASubscriptionPlan()
    {
        db.TopUpProducts.Add(new TopUpProduct { Slug = "s_cc_1800", Title = "coins", Price = 9.69m, Cost = 1800, CurrencyCode = "eur",
            ProviderSlug = "stripe", Type = Product.ProductType.TOP_UP });
        db.SaveChanges();
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "s_cc_1800", Options("DE")));
    }

    [Test]
    public async Task PayPalSubscriptionIsRecordedForTheWebhookWithItsPrice()
    {
        var response = await Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(response.DirectLink, Is.EqualTo("https://www.paypal.com/approve"));
        Assert.That(paypalRequest.PlanId, Is.EqualTo("P-PREMIUM"));
        Assert.That(paypalRequest.Price, Is.EqualTo(9.69m));
        Assert.That(paypalRequest.CustomId, Is.EqualTo(SubscriptionService.PayPalCustomId(premium.Id, "buyer")));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Provider, Is.EqualTo("paypal"));
        Assert.That(request.SessionId, Is.EqualTo("I-SUB"));
        Assert.That(request.Amount, Is.EqualTo(9.69m));
    }

    /// <summary>
    /// Regression: an open checkout was reused for the same product regardless of provider, which would hand
    /// a PayPal subscription id to Stripe as a checkout session.
    /// </summary>
    [Test]
    public async Task OpenCheckoutIsOnlyReusedForTheSameProvider()
    {
        var controller = Controller("DE");
        await controller.PayPalSubscribe("buyer", "l_premium", Options("DE"));
        var response = await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));
        Assert.That(response.Id, Is.EqualTo("cs_1"));
        stripe.Verify(s => s.GetCheckoutSession(It.IsAny<string>()), Times.Never);
        Assert.That(await db.PaymentRequests.Select(r => r.Provider).ToListAsync(), Is.EquivalentTo(new[] { "paypal", "stripe" }));
    }

    [Test]
    public void PayPalSubscriptionsAreDirectSalesOnly()
    {
        Assert.ThrowsAsync<ApiException>(() => Controller("BR").PayPalSubscribe("buyer", "l_premium", Options("BR")));
        paypal.Verify(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()), Times.Never);
    }

    [Test]
    public void PlanWithoutPayPalPlanIsNotSoldViaPayPal()
    {
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").PayPalSubscribe("buyer", "l_prem_plus", Options("DE")));
    }

    [Test]
    public void LemonSqueezySubscriptionIsRejectedWhereADirectProviderApplies()
    {
        var error = Assert.ThrowsAsync<ApiException>(() => Controller("DE").LemonSqueezySubscribe("buyer", "l_premium", Options("DE")));
        Assert.That(error.Message, Does.Contain("payment methods offered for your country"));
    }

    [Test]
    public void ProviderListOnlyNamesPayPalForPlansWithAPayPalPlan()
    {
        var controller = Controller("DE");
        Assert.That(controller.GetSubscriptionProviders("DE", "l_premium").Select(p => p.Provider), Is.EqualTo(new[] { "stripe", "paypal" }));
        Assert.That(controller.GetSubscriptionProviders("DE", "l_prem_plus").Select(p => p.Provider), Is.EqualTo(new[] { "stripe" }));
        Assert.That(controller.GetSubscriptionProviders("BR").Single().Mode, Is.EqualTo("managed"));
    }

    [Test]
    public async Task DiscountValidationUsesLocalCodes()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "SUBONLY", AmountType = "fixed", Amount = 100, SubscriptionOnly = true });
        db.DiscountCodes.Add(new DiscountCode { Code = "OLD", AmountType = "percent", Amount = 5, ExpiresAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        var controller = Controller("DE");

        var forSubscription = (OkObjectResult)(await controller.ValidateDiscountCode("subonly", true, "stripe")).Result;
        Assert.That(((Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)forSubscription.Value).IsValid, Is.True);
        var oneTime = (OkObjectResult)(await controller.ValidateDiscountCode("subonly", false, "stripe")).Result;
        Assert.That(((Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)oneTime.Value).IsValid, Is.False);
        var expired = (OkObjectResult)(await controller.ValidateDiscountCode("old", true, "paypal")).Result;
        Assert.That(((Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)expired.Value).IsValid, Is.False);
    }
}
