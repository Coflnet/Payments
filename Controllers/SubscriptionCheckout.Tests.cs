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
    private readonly List<PayPalPlanRequest> paypalPlans = [];
    private readonly List<StripeCouponSpec> coupons = [];
    private readonly List<(string Country, int BasisPoints)> taxRates = [];

    [SetUp]
    public void SetUpGateways()
    {
        paypalPlans.Clear();
        coupons.Clear();
        taxRates.Clear();
        stripe = new Mock<IStripeSubscriptionGateway>();
        stripe.Setup(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()))
            .Callback<SessionCreateOptions>(o => created = o)
            .ReturnsAsync(new Session { Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1" });
        stripe.Setup(s => s.EnsureFirstPaymentCoupon(It.IsAny<StripeCouponSpec>()))
            .Callback<StripeCouponSpec>(coupons.Add).ReturnsAsync((StripeCouponSpec c) => c.Id);
        stripe.Setup(s => s.EnsureVatTaxRate(It.IsAny<string>(), It.IsAny<int>()))
            .Callback<string, int>((c, bp) => taxRates.Add((c, bp))).ReturnsAsync((string c, int bp) => $"txr_{c}_{bp}");
        lemonSqueezy = new Mock<LemonSqueezyService>(new ConfigurationBuilder().Build(), NullLogger<LemonSqueezyService>.Instance,
            db, new VariantCacheService(NullLogger<VariantCacheService>.Instance));
        lemonSqueezy.Setup(l => l.ValidateDiscountCodeAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync((Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount)null);
        lemonSqueezy.Setup(l => l.ValidateDiscountCodeAsync("LSCODE", It.IsAny<bool?>()))
            .ReturnsAsync(new Coflnet.Payments.Models.LemonSqueezy.ValidatedDiscount
                { Code = "LSCODE", Id = "77", AmountType = "percent", Amount = 10, IsValid = true });
        paypal = new Mock<IPayPalSubscriptionApi>();
        paypal.Setup(p => p.CreateCatalogProduct(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync("PROD-1");
        paypal.Setup(p => p.CreatePlan(It.IsAny<PayPalPlanRequest>()))
            .Callback<PayPalPlanRequest>(r => paypalPlans.Add(r))
            .ReturnsAsync("P-PREMIUM");
        paypal.Setup(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()))
            .Callback<PayPalSubscriptionRequest>(r => paypalRequest = r)
            .ReturnsAsync(new PayPalSubscription
            {
                Id = "I-SUB", Status = "APPROVAL_PENDING",
                Links = [new PayPalLink { Rel = "approve", Href = "https://www.paypal.com/approve" }]
            });
    }

    private TopUpController Controller(string ipCountry, Dictionary<string, string> settings = null, int? activeDirectUs = null)
    {
        var values = new Dictionary<string, string>(VatRates)
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
            new DiscountCodeService(db, NullLogger<DiscountCodeService>.Instance),
            new SubscriptionRouting(config, activeDirectUs is int active ? new UsDirectSubscriptionCap(config, new FixedCounter(active)) : null),
            stripe.Object, paypal.Object);
    }

    /// <summary>The count of the state "CA" (the others have none); a negative count fails (the counts stay unknown)</summary>
    private sealed class FixedCounter(int count) : IDirectSubscriptionCounter
    {
        public Task<IReadOnlyDictionary<string, int>> CountActiveDirectByRegion(string country) =>
            count < 0 ? throw new InvalidOperationException("db down")
                : Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { ["CA"] = count });
    }

    private static TopUpOptions Options(string country, string discount = null, string region = null) => new()
    {
        Country = country, UserIp = "203.0.113.7", SuccessUrl = "https://ok", CancelUrl = "https://cancel", DiscountCode = discount,
        Region = region
    };

    [Test]
    public async Task EuBuyerGetsADirectStripeSubscriptionAtTheNetPlanPricePlusVat()
    {
        var response = await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(response.DirectLink, Is.EqualTo("https://checkout.stripe.com/c/cs_1"));
        Assert.That(created.ManagedPayments, Is.Null);
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1153));
        Assert.That(created.LineItems.Single().PriceData.Recurring.IntervalCount, Is.EqualTo(4));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Provider, Is.EqualTo("stripe"));
        Assert.That(request.SessionId, Is.EqualTo("cs_1"));
        Assert.That((await Buyer()).Country, Is.EqualTo("DE"));
    }

    /// <summary>
    /// Owner decision: from the 10th active direct US subscription on, US buyers get Stripe Managed Payments (Stripe collects
    /// the sales tax) and no direct Stripe or PayPal checkout; providers, discount/auto and the checkouts all agree.
    /// </summary>
    [Test]
    public async Task UsBuyersGoToManagedPaymentsOnceTheDirectCapIsReached()
    {
        var controller = Controller("US", activeDirectUs: 10);

        var providers = await controller.GetSubscriptionProviders("US", region: "CA");
        await controller.StripeSubscribe("buyer", "l_premium", Options("US", region: "CA"));

        Assert.That(providers.Select(p => (p.Provider, p.Mode)), Is.EqualTo(new[] { ("stripe", "managed") }));
        Assert.That(created.ManagedPayments?.Enabled, Is.True);
        Assert.That(created.LineItems.Single().PriceData.TaxBehavior, Is.EqualTo("exclusive"));
        Assert.That(created.SubscriptionData.Metadata["mode"], Is.EqualTo("managed"));
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options("US", region: "CA")));
    }

    /// <summary>
    /// Owner decision: the cap of 10 applies per US state: 10 direct subscriptions in CA send CA buyers to Managed Payments,
    /// NY buyers (no subscriptions there) stay direct, and a US buyer without a known state is treated as capped.
    /// </summary>
    [Test]
    public async Task TheUsCapAppliesPerStateAndAnUnknownStateIsCapped()
    {
        var controller = Controller("US", activeDirectUs: 10);

        var ca = await controller.GetSubscriptionProviders("US", region: "ca");
        var ny = await controller.GetSubscriptionProviders("US", region: "NY");
        var unknown = await controller.GetSubscriptionProviders("US");
        var invalid = await controller.GetSubscriptionProviders("US", region: "California");

        Assert.That(ca.Select(p => p.Mode), Is.EqualTo(new[] { "managed" }));
        Assert.That(ny.Select(p => (p.Provider, p.Mode)), Is.EqualTo(new[] { ("stripe", "direct"), ("paypal", "direct") }));
        Assert.That(unknown.Select(p => p.Mode), Is.EqualTo(new[] { "managed" }));
        Assert.That(invalid.Select(p => p.Mode), Is.EqualTo(new[] { "managed" }));
    }

    [Test]
    public async Task TheCheckoutStateIsStoredOnTheRequestAndTheStripeMetadata()
    {
        await Controller("US", activeDirectUs: 10).StripeSubscribe("buyer", "l_premium", Options("US", region: "ny"));

        Assert.That(created.ManagedPayments, Is.Null);
        Assert.That(created.SubscriptionData.Metadata["region"], Is.EqualTo("NY"));
        Assert.That((await db.PaymentRequests.SingleAsync()).CheckoutRegion, Is.EqualTo("NY"));
    }

    [Test]
    public async Task AnUnknownStateGetsManagedPaymentsAtCheckoutAndStoresNoRegion()
    {
        var controller = Controller("US", activeDirectUs: 0);

        await controller.StripeSubscribe("buyer", "l_premium", Options("US"));

        Assert.That(created.ManagedPayments?.Enabled, Is.True);
        Assert.That(created.SubscriptionData.Metadata.ContainsKey("region"), Is.False);
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options("US")));
    }

    /// <summary>
    /// Regression: TR, SA, KR, VN, MX and MD were blocked for Managed Payments only for tax reasons that Stripe (merchant of
    /// record) takes over. They get Managed Payments and are still refused for direct and one-time sales.
    /// </summary>
    [TestCase("TR")]
    [TestCase("SA")]
    [TestCase("KR")]
    [TestCase("VN")]
    [TestCase("MX")]
    [TestCase("MD")]
    public async Task TaxBlockedCountriesCoveredByStripeGetManagedPaymentsOnly(string country)
    {
        var controller = Controller(country);
        await AddBlackFridayCode();

        var providers = await controller.GetSubscriptionProviders(country);
        var auto = (await controller.GetAutoDiscount(country)).Result as OkObjectResult;
        await controller.StripeSubscribe("buyer", "l_premium", Options(country));

        Assert.That(providers.Select(p => (p.Provider, p.Mode)), Is.EqualTo(new[] { ("stripe", "managed") }));
        Assert.That(((AutoDiscount)auto.Value).Providers, Is.EqualTo(new[] { "stripe" }));
        Assert.That(created.ManagedPayments?.Enabled, Is.True);
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options(country)));
        Assert.That(SalesRestrictions.IsCountryBlocked(country), Is.True, "still blocked where Coflnet is the seller (one-time purchases)");
        Assert.That(SalesRestrictions.DoWeSellTo(country, null), Is.False);
    }

    [Test]
    public void AManagedBlockedCountryIsRefusedEvenThoughStripeCoversIt()
    {
        var controller = Controller("TR", new() { ["SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES"] = "tr" });

        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options("TR")));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    /// <summary>The IP country of a managed buyer must be covered too; TR is (only tax-blocked), so a buyer located there may buy managed</summary>
    [Test]
    public async Task ManagedPaymentsAcceptsATaxBlockedIpCountry()
    {
        await Controller("TR").StripeSubscribe("buyer", "l_premium", Options("CH"));

        stripe.Verify(s => s.CreateCheckoutSession(It.Is<SessionCreateOptions>(o => o.ManagedPayments.Enabled == true)), Times.Once);
    }

    [Test]
    public async Task TheCapOnlyAffectsTheUsAndDiscountAutoAgreesWithTheProviders()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "BF", AmountType = "percent", Amount = 25, SubscriptionOnly = true, AutoApply = true, IsActive = true });
        await db.SaveChangesAsync();
        var controller = Controller("US", activeDirectUs: 10);

        var us = (await controller.GetAutoDiscount("US", "CA")).Result as OkObjectResult;
        var usOtherState = (await controller.GetAutoDiscount("US", "NY")).Result as OkObjectResult;
        var de = (await controller.GetAutoDiscount("DE")).Result as OkObjectResult;

        Assert.That(((AutoDiscount)us.Value).Providers, Is.EqualTo(new[] { "stripe" }));
        Assert.That(((AutoDiscount)usOtherState.Value).Providers, Is.EqualTo(new[] { "stripe", "paypal" }));
        Assert.That(((AutoDiscount)de.Value).Providers, Is.EqualTo(new[] { "stripe", "paypal" }));
        Assert.That((await controller.GetSubscriptionProviders("DE")).Select(p => p.Mode), Is.All.EqualTo("direct"));
    }

    [Test]
    public async Task UsBuyersStayDirectBelowTheCapAndTheLimitIsConfigurable()
    {
        Assert.That((await Controller("US", activeDirectUs: 9).GetSubscriptionProviders("US", region: "CA")).Select(p => p.Mode), Is.All.EqualTo("direct"));
        var limited = Controller("US", new() { ["SUBSCRIPTIONS:US_DIRECT_LIMIT"] = "3" }, activeDirectUs: 3);
        Assert.That((await limited.GetSubscriptionProviders("US", region: "CA")).Select(p => p.Mode), Is.EqualTo(new[] { "managed" }));
    }

    [Test]
    public async Task ManagedPaymentsSendsTheNetPriceWithExclusiveTax()
    {
        await Controller("NO").StripeSubscribe("buyer", "l_premium", Options("NO"));
        var price = created.LineItems.Single().PriceData;
        Assert.That(price.UnitAmount, Is.EqualTo(969));
        Assert.That(price.TaxBehavior, Is.EqualTo("exclusive"));
        Assert.That(created.SubscriptionData.Metadata["priceCents"], Is.EqualTo("969"));
    }

    [Test]
    public async Task DirectStripeSessionLetsStripeAddNoTax()
    {
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));
        Assert.That(created.AutomaticTax, Is.Null);
        Assert.That(created.LineItems.Single().PriceData.TaxBehavior, Is.Null);
        Assert.That(created.LineItems.Single().PriceData.ProductData.TaxCode, Is.Null);
    }

    [Test]
    public async Task GermanYearlyPlanWithAHalfOffPeriodCodeAddsVatToTheDiscountedNetPrice()
    {
        await AddPlans();
        db.DiscountCodes.Add(new DiscountCode
        {
            Code = "HALF", AmountType = "percent", Amount = 0, SubscriptionOnly = true, PeriodAmounts = new() { [52] = 50 }
        });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium_52", Options("DE", "HALF"));
        // net 99.69 -> 49.84 after the code (see PeriodAmountOverrides...), gross = 49.84 * 1.19 = 59.3096
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(5931));
    }

    [Test]
    public async Task BritishBuyerPaysTwentyPercentVat()
    {
        await Controller("GB").StripeSubscribe("buyer", "l_premium", Options("GB"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1163), "9.69 * 1.20 = 11.628");
    }

    [Test]
    public async Task AmericanBuyerPaysThePlanPriceWithoutVat()
    {
        await Controller("US").StripeSubscribe("buyer", "l_premium", Options("US"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(969));
    }

    /// <summary>
    /// Regression: a direct country without a VAT rate would be sold at the net price; it must be refused and not listed.
    /// </summary>
    [Test]
    public async Task DirectCountryWithoutAVatRateIsRefusedAndNotListed()
    {
        var controller = Controller("FR");
        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options("FR")));
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options("FR")));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
        paypal.Verify(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()), Times.Never);

        var listed = await controller.GetSubscriptionProviders("FR");
        Assert.That(listed.Select(o => o.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
        Assert.That(controller.GetAutoDiscount("FR").Result.Result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task PayPalFirstPaymentOnlyCodeAndRenewalsAreBothGross()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        await Controller("GB").PayPalSubscribe("buyer", "l_premium", Options("GB", "INTRO"));
        Assert.That(paypalRequest.Price, Is.EqualTo(5.81m), "4.84 * 1.20 = 5.808");
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(11.63m));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That((request.Amount, request.RecurringAmount), Is.EqualTo((5.81m, (decimal?)11.63m)));
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
        await Controller("NO").StripeSubscribe("buyer", "l_premium", Options("NO"));
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        Assert.That(created.LineItems.Single().PriceData.ProductData.TaxCode, Is.EqualTo("txcd_10103000"));
        Assert.That(created.SubscriptionData.Metadata["mode"], Is.EqualTo("managed"));
    }

    [Test]
    public void ManagedPaymentsCanBeSwitchedOff()
    {
        var controller = Controller("NO", new() { ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "false" });
        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options("NO")));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    [Test]
    public async Task LocalDiscountCodeLowersTheRecurringPrice()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "SAVE10", AmountType = "percent", Amount = 10, SubscriptionOnly = true });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "save10"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1038));
        Assert.That(created.SubscriptionData.Metadata["priceCents"], Is.EqualTo("1038"));
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

    private async Task<DiscountCode> AddCodeWithRedemptions(int max, int redeemed = 0)
    {
        var code = new DiscountCode { Code = "LIMITED", AmountType = "percent", Amount = 10, MaxRedemptions = max, TimesRedeemed = redeemed };
        db.DiscountCodes.Add(code);
        await db.SaveChangesAsync();
        return code;
    }

    private async Task<int> TimesRedeemed() => (await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed;

    [Test]
    public async Task NewCheckoutRedeemsTheCodeOnce()
    {
        await AddCodeWithRedemptions(5);

        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "LIMITED"));

        Assert.That(await TimesRedeemed(), Is.EqualTo(1));
    }

    [Test]
    public async Task ReusedCheckoutDoesNotRedeemTheCodeAgain()
    {
        await AddCodeWithRedemptions(5);
        var controller = Controller("DE");
        await controller.StripeSubscribe("buyer", "l_premium", Options("DE", "LIMITED"));
        stripe.Setup(s => s.GetCheckoutSession("cs_1")).ReturnsAsync(new Session { Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1" });

        var again = await controller.StripeSubscribe("buyer", "l_premium", Options("DE", "LIMITED"));

        Assert.That(again.Id, Is.EqualTo("cs_1"));
        Assert.That(await TimesRedeemed(), Is.EqualTo(1));
    }

    [Test]
    public async Task FailedStripeCheckoutLeavesTheRedemptionsUnchanged()
    {
        await AddCodeWithRedemptions(5, redeemed: 2);
        stripe.Setup(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>())).ThrowsAsync(new InvalidOperationException("403"));

        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "LIMITED")));

        Assert.That(await TimesRedeemed(), Is.EqualTo(2));
    }

    [Test]
    public async Task FailedPayPalCheckoutLeavesTheRedemptionsUnchanged()
    {
        await AddCodeWithRedemptions(5);
        paypal.Setup(p => p.Create(It.IsAny<PayPalSubscriptionRequest>())).ThrowsAsync(new InvalidOperationException("paypal down"));

        Assert.ThrowsAsync<InvalidOperationException>(() => Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "LIMITED")));

        Assert.That(await TimesRedeemed(), Is.Zero);
    }

    [Test]
    public async Task PayPalWithoutApprovalLinkLeavesTheRedemptionsUnchanged()
    {
        await AddCodeWithRedemptions(5);
        paypal.Setup(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()))
            .ReturnsAsync(new PayPalSubscription { Id = "I-SUB", Status = "APPROVAL_PENDING", Links = [] });

        Assert.ThrowsAsync<ApiException>(() => Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "LIMITED")));

        Assert.That(await TimesRedeemed(), Is.Zero);
    }

    [Test]
    public async Task ExhaustedCodeIsStillRefusedAndNotCountedPastTheCap()
    {
        await AddCodeWithRedemptions(2, redeemed: 2);

        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "LIMITED")));

        Assert.That(await TimesRedeemed(), Is.EqualTo(2));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    [Test]
    public async Task RedeemStopsAtTheCapAndReleaseNeverGoesBelowZero()
    {
        await AddCodeWithRedemptions(1);
        var service = new DiscountCodeService(db, NullLogger<DiscountCodeService>.Instance);
        Assert.That(await service.Redeem("LIMITED"), Is.True);
        Assert.That(await service.Redeem("LIMITED"), Is.False);
        await service.Release("LIMITED");
        await service.Release("LIMITED");

        Assert.That(await TimesRedeemed(), Is.Zero, "release never goes below 0");
    }

    [Test]
    public async Task FirstPaymentOnlyCodeDiscountsOnlyThePayPalTrialCycle()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        await Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "INTRO"));
        Assert.That(paypalRequest.Price, Is.EqualTo(5.76m));
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(11.53m));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Amount, Is.EqualTo(5.76m));
        Assert.That(request.RecurringAmount, Is.EqualTo(11.53m));
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
        Assert.That(paypalRequest.Price, Is.EqualTo(11.53m));
        Assert.That(paypalRequest.CustomId, Is.EqualTo(SubscriptionService.PayPalCustomId(premium.Id, "buyer")));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Provider, Is.EqualTo("paypal"));
        Assert.That(request.SessionId, Is.EqualTo("I-SUB"));
        Assert.That(request.Amount, Is.EqualTo(11.53m));
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

    /// <summary>
    /// Regression: an open checkout of the same plan was reused regardless of its price, so a buyer who opened a
    /// full-price checkout and then applied the (auto) promotion code was sent to the full-price session (and the
    /// other way round: a promotion session was handed out again after the code expired).
    /// </summary>
    [Test]
    public async Task OpenCheckoutIsNotReusedForAnotherPrice()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "SAVE10", AmountType = "percent", Amount = 10, SubscriptionOnly = true });
        await db.SaveChangesAsync();
        var controller = Controller("DE");
        await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));

        await controller.StripeSubscribe("buyer", "l_premium", Options("DE", "SAVE10"));

        stripe.Verify(s => s.GetCheckoutSession(It.IsAny<string>()), Times.Never);
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1038));
        Assert.That(await db.PaymentRequests.Select(r => r.Amount).ToListAsync(), Is.EquivalentTo(new[] { 11.53m, 10.38m }));
    }

    /// <summary>
    /// Regression: a "first payment only" and a "forever" code with the same first price shared one open PayPal
    /// checkout, so the buyer got the renewal price of the other code.
    /// </summary>
    [Test]
    public async Task OpenPayPalCheckoutIsNotReusedForAnotherRenewalPrice()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "ONCE", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        db.DiscountCodes.Add(new DiscountCode { Code = "FOREVER", AmountType = "percent", Amount = 50 });
        await db.SaveChangesAsync();
        var controller = Controller("DE");
        await controller.PayPalSubscribe("buyer", "l_premium", Options("DE", "ONCE"));

        await controller.PayPalSubscribe("buyer", "l_premium", Options("DE", "FOREVER"));

        paypal.Verify(p => p.Get(It.IsAny<string>()), Times.Never);
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(5.76m));
        Assert.That(await db.PaymentRequests.Select(r => r.RecurringAmount).ToListAsync(), Is.EquivalentTo(new decimal?[] { 11.53m, null }));
    }

    /// <summary>
    /// Owner decision: a checkout started before its code expired stays payable for 24 hours, so the Stripe session
    /// keeps Stripe's default lifetime (24 hours) instead of ending with the code; the code's expiry is recorded.
    /// </summary>
    [TestCase("DE")]
    [TestCase("NO")]
    public async Task StripeSessionWithAnExpiringCodeKeepsStripesDefaultLifetime(string country)
    {
        var expires = DateTime.UtcNow.AddMinutes(5);
        db.DiscountCodes.Add(new DiscountCode { Code = "PROMO", AmountType = "percent", Amount = 25, ExpiresAt = expires });
        await db.SaveChangesAsync();

        await Controller(country).StripeSubscribe("buyer", "l_premium", Options(country, "PROMO"));

        Assert.That(created.ExpiresAt, Is.Null);
        Assert.That((await db.PaymentRequests.SingleAsync()).ExpiresAt, Is.EqualTo(expires).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task ExpiredCodeIsNotAcceptedForNewCheckouts()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "PROMO", AmountType = "percent", Amount = 25, ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "PROMO")));
        Assert.ThrowsAsync<ApiException>(() => Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "PROMO")));
    }

    /// <summary>
    /// Regression: the open checkout was matched by amount only, so a buyer who opened a direct US checkout (9.69, no VAT)
    /// and then one from a Managed Payments country (9.69 net, Stripe adds tax) got the direct session without tax.
    /// </summary>
    [Test]
    public async Task OpenCheckoutOfTheSameAmountIsNotReusedForAnotherCountry()
    {
        await Controller("US").StripeSubscribe("buyer", "l_premium", Options("US"));

        await Controller("NO").StripeSubscribe("buyer", "l_premium", Options("NO"));

        stripe.Verify(s => s.GetCheckoutSession(It.IsAny<string>()), Times.Never);
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        var requests = await db.PaymentRequests.OrderBy(r => r.Id).Select(r => new { r.Amount, r.Country }).ToListAsync();
        Assert.That(requests.Select(r => (r.Amount, r.Country)), Is.EqualTo(new[] { (9.69m, "US"), (9.69m, "NO") }));
    }

    /// <summary>
    /// Regression: a direct and a Managed Payments US checkout have the same amount, country and provider, so an open direct
    /// US session was reused after the cap flipped and the buyer stayed on the direct (untaxed) checkout.
    /// </summary>
    [Test]
    public async Task OpenDirectUsCheckoutIsNotReusedOnceTheCapSendsUsBuyersToManagedPayments()
    {
        await Controller("US", activeDirectUs: 9).StripeSubscribe("buyer", "l_premium", Options("US", region: "CA"));
        Assert.That(created.ManagedPayments, Is.Null);

        await Controller("US", activeDirectUs: 10).StripeSubscribe("buyer", "l_premium", Options("US", region: "CA"));

        stripe.Verify(s => s.GetCheckoutSession(It.IsAny<string>()), Times.Never);
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        var requests = await db.PaymentRequests.OrderBy(r => r.Id).Select(r => r.ManagedPayments).ToListAsync();
        Assert.That(requests, Is.EqualTo(new[] { false, true }));
    }

    /// <summary>
    /// Until the first count succeeded the cap counts as reached: US buyers get Managed Payments, not a direct checkout.
    /// </summary>
    [Test]
    public async Task UsBuyersGoToManagedPaymentsWhileTheCountIsUnknown()
    {
        var controller = Controller("US", activeDirectUs: -1);

        var providers = await controller.GetSubscriptionProviders("US", region: "CA");

        Assert.That(providers.Select(p => (p.Provider, p.Mode)), Is.EqualTo(new[] { ("stripe", "managed") }));
    }

    /// <summary>
    /// The PayPal webhook splits the VAT with the checkout's country and takes the codes from the checkout
    /// (regression: the creator code was lost, so PayPal subscriptions earned creators nothing).
    /// </summary>
    [Test]
    public async Task PayPalCheckoutRecordsItsVerifiedCountryAndCodes()
    {
        db.CreatorCodes.Add(new Coflnet.Payments.Models.CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 0, RevenueSharePercent = 10, IsActive = true });
        db.DiscountCodes.Add(new DiscountCode { Code = "SAVE10", AmountType = "percent", Amount = 10, SubscriptionOnly = true });
        await db.SaveChangesAsync();
        var options = Options("AT", "SAVE10");
        options.CreatorCode = "CREATOR";

        await Controller("AT").PayPalSubscribe("buyer", "l_premium", options);

        var request = await db.PaymentRequests.SingleAsync();
        Assert.That((request.Amount, request.Country), Is.EqualTo((10.46m, "AT")), "9.69 - 10% = 8.72, * 1.20 = 10.464");
        Assert.That((request.CreatorCode, request.DiscountCode), Is.EqualTo(("CREATOR", "SAVE10")));
    }

    [Test]
    public async Task OpenCheckoutOfTheSamePriceIsReused()
    {
        stripe.Setup(s => s.GetCheckoutSession("cs_1")).ReturnsAsync(new Session { Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1" });
        var controller = Controller("DE");
        await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));

        var again = await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(again.Id, Is.EqualTo("cs_1"));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Once);
        Assert.That(await db.PaymentRequests.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public void PayPalSubscriptionsAreDirectSalesOnly()
    {
        Assert.ThrowsAsync<ApiException>(() => Controller("NO").PayPalSubscribe("buyer", "l_premium", Options("NO")));
        paypal.Verify(p => p.Create(It.IsAny<PayPalSubscriptionRequest>()), Times.Never);
    }

    [Test]
    public void LemonSqueezySubscriptionIsRejectedWhereADirectProviderApplies()
    {
        var error = Assert.ThrowsAsync<ApiException>(() => Controller("DE").LemonSqueezySubscribe("buyer", "l_premium", Options("DE")));
        Assert.That(error.Message, Does.Contain("payment methods offered for your country"));
    }

    [Test]
    public async Task ProviderListNamesPayPalForEverySubscriptionPlan()
    {
        var controller = Controller("DE");
        Assert.That((await controller.GetSubscriptionProviders("DE", "l_premium")).Select(p => p.Provider), Is.EqualTo(new[] { "stripe", "paypal" }));
        Assert.That((await controller.GetSubscriptionProviders("DE", "l_prem_plus")).Select(p => p.Provider), Is.EqualTo(new[] { "stripe", "paypal" }));
        Assert.That((await controller.GetSubscriptionProviders("DE", "unknown")).Select(p => p.Provider), Is.EqualTo(new[] { "stripe" }));
        Assert.That((await controller.GetSubscriptionProviders("NO")).Single().Mode, Is.EqualTo("managed"));
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

    private const int ThirteenWeeks = 7776000;
    private const int FiftyTwoWeeks = 31536000;

    private async Task AddPlans()
    {
        db.TopUpProducts.AddRange(
            new TopUpProduct { Slug = "l_premium_13", Title = "13 weeks", Price = 29.69m, Cost = 5400, CurrencyCode = "eur",
                ProviderSlug = "lemonsqueezy", OwnershipSeconds = ThirteenWeeks, Type = Product.ProductType.SERVICE },
            new TopUpProduct { Slug = "l_premium_52", Title = "52 weeks", Price = 99.69m, Cost = 23400, CurrencyCode = "eur",
                ProviderSlug = "lemonsqueezy", OwnershipSeconds = FiftyTwoWeeks, Type = Product.ProductType.SERVICE },
            new TopUpProduct { Slug = "l_premium-slots-4", Title = "4 slots", Price = 33.69m, Cost = 7200, CurrencyCode = "eur",
                ProviderSlug = "lemonsqueezy", OwnershipSeconds = FourWeeks, SlotCount = 4, SlotTier = "premium", Type = Product.ProductType.SERVICE });
        await db.SaveChangesAsync();
        // As provisioned: every period of a tier is in its tier group (the 4-week plan is what the longer ones continue as)
        foreach (var slug in new[] { "l_premium_13", "l_premium_52" })
            await groups.AddProductToGroup(await db.TopUpProducts.SingleAsync(p => p.Slug == slug), "premium");
    }

    private async Task AddBlackFridayCode(string duration = DiscountCode.EveryRenewal)
    {
        db.DiscountCodes.Add(new DiscountCode
        {
            Code = "BF2026", AmountType = "percent", Amount = 10, SubscriptionOnly = true, AutoApply = true, Duration = duration,
            PeriodAmounts = new() { [4] = 25, [13] = 50, [52] = 50 }
        });
        await db.SaveChangesAsync();
    }

    [TestCase("l_premium", 865)]
    [TestCase("l_premium_13", 1766)]
    [TestCase("l_premium_52", 5931)]
    public async Task PeriodAmountOverridesTheDefaultAmountOfDirectStripe(string slug, long cents)
    {
        await AddPlans();
        await AddBlackFridayCode();
        await Controller("DE").StripeSubscribe("buyer", slug, Options("DE", "bf2026"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(cents));
    }

    [Test]
    public async Task PeriodAmountAlsoAppliesToManagedPayments()
    {
        await AddBlackFridayCode();
        await Controller("NO").StripeSubscribe("buyer", "l_premium", Options("NO", "BF2026"));
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(727), "net: Stripe adds the tax");
    }

    [Test]
    public async Task PeriodWithoutAnEntryUsesTheDefaultAmount()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "ONLY13", AmountType = "percent", Amount = 10, PeriodAmounts = new() { [13] = 50 } });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "ONLY13"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1038));
    }

    [Test]
    public async Task PeriodAmountAppliesToPayPalPrices()
    {
        await AddPlans();
        await AddBlackFridayCode();
        await Controller("DE").PayPalSubscribe("buyer", "l_premium_13", Options("DE", "BF2026"));
        Assert.That(paypalRequest.Price, Is.EqualTo(17.66m));
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(8.65m), "then every 4 weeks: l_premium 9.69 - 25% (the 4-week amount) + 19% VAT");
    }

    [Test]
    public async Task ValidationReturnsThePeriodAmountsAndKeepsTheDefaultAmount()
    {
        await AddBlackFridayCode();
        var result = Value(await Controller("DE").ValidateDiscountCode("bf2026", true, "stripe"));
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Amount, Is.EqualTo(10));
        Assert.That(result.PeriodAmounts, Is.EqualTo(new Dictionary<int, decimal> { [4] = 25, [13] = 50, [52] = 50 }));
    }

    [Test]
    public async Task SlotBundleAcceptsDiscountAndCreatorCodeLikeASingleSubscription()
    {
        await AddPlans();
        await AddBlackFridayCode();
        db.CreatorCodes.Add(new Coflnet.Payments.Models.CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 10, IsActive = true });
        await db.SaveChangesAsync();
        var options = Options("DE", "BF2026");
        options.CreatorCode = "CREATOR";

        await Controller("DE").StripeSubscribe("buyer", "l_premium-slots-4", options);

        // 33.69 - 10% creator = 30.32, then -25% = 22.74
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(2706));
        Assert.That(created.SubscriptionData.Metadata["productSlug"], Is.EqualTo("l_premium-slots-4"));
        Assert.That(created.SubscriptionData.Metadata["discountCode"], Is.EqualTo("BF2026"));
        Assert.That(created.SubscriptionData.Metadata["creatorCode"], Is.EqualTo("CREATOR"));
    }

    [Test]
    public async Task SlotBundleIsCreatedAtPayPalWithItsCodes()
    {
        await AddPlans();
        await AddBlackFridayCode();
        await Controller("DE").PayPalSubscribe("buyer", "l_premium-slots-4", Options("DE", "BF2026"));
        Assert.That(paypalRequest.Price, Is.EqualTo(30.07m));
        Assert.That(paypalPlans.Single().Price, Is.EqualTo(33.69m));
    }

    [Test]
    public async Task AutoApplyEndpointReturnsTheNewestActiveCode()
    {
        db.DiscountCodes.AddRange(
            Auto("OLDER", createdAt: DateTime.UtcNow.AddDays(-2)),
            Auto("NEWER", createdAt: DateTime.UtcNow.AddDays(-1)),
            Auto("EXPIRED", expiresAt: DateTime.UtcNow.AddHours(-1)),
            Auto("FUTURE", startsAt: DateTime.UtcNow.AddHours(1)),
            Auto("OFF", active: false),
            Auto("USEDUP", maxRedemptions: 1, redeemed: 1));
        await db.SaveChangesAsync();

        var result = (AutoDiscount)((OkObjectResult)(await Controller("DE").GetAutoDiscount("DE")).Result).Value;

        Assert.That(result.Code, Is.EqualTo("NEWER"));
        Assert.That(result.PeriodAmounts[13], Is.EqualTo(50));
        Assert.That(result.Duration, Is.EqualTo("forever"));
        Assert.That(result.Providers, Is.EqualTo(new[] { "stripe", "paypal" }));
    }

    [Test]
    public async Task AutoApplyEndpointIsEmptyWithoutAnActivePromotion()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "MANUAL", AmountType = "percent", Amount = 10, SubscriptionOnly = true });
        await db.SaveChangesAsync();
        Assert.That((await Controller("DE").GetAutoDiscount()).Result, Is.TypeOf<NoContentResult>());
    }

    /// <summary>
    /// A buyer routed to Lemon Squeezy (no direct provider applies) is never shown the promotion, and a local code sent
    /// to a Lemon Squeezy checkout is rejected rather than charged at another price than shown.
    /// </summary>
    [Test]
    public async Task LemonSqueezyBuyersAreNotOfferedTheLocalPromotion()
    {
        await AddBlackFridayCode();
        var controller = Controller("DE", new() { ["SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED"] = "false", ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "false",
            ["SUBSCRIPTIONS:PAYPAL_ENABLED"] = "false" });
        Assert.That((await controller.GetAutoDiscount("DE")).Result, Is.TypeOf<NoContentResult>());
        var error = Assert.ThrowsAsync<ApiException>(() => controller.LemonSqueezySubscribe("buyer", "l_premium", Options("DE", "BF2026")));
        Assert.That(error.Message, Does.Contain("Invalid or expired"));
    }

    [Test]
    public async Task AutoApplyCodePassesNormalValidationWithTheCheckout()
    {
        await AddBlackFridayCode();
        var offered = (AutoDiscount)((OkObjectResult)(await Controller("DE").GetAutoDiscount("DE")).Result).Value;
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", offered.Code));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(865));
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.EqualTo(1));
    }

    private async Task AddOnceCode()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Regression: the recurring unit amount of the session was the discounted first price (raised after the first invoice),
    /// so Stripe's page showed the discount as the renewal price. Now the full gross price recurs and a one-time coupon of the
    /// difference lowers the first invoice.
    /// </summary>
    [Test]
    public async Task DirectFirstPaymentOnlyCodeRecursAtTheFullGrossPriceWithAOneTimeCoupon()
    {
        await AddOnceCode();

        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "INTRO"));

        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1153));
        Assert.That(created.Discounts.Single().Coupon, Is.EqualTo("coflnet_once_577_eur"));
        Assert.That(coupons.Single(), Is.EqualTo(new StripeCouponSpec("coflnet_once_577_eur", 577, null, "eur")));
        Assert.That(created.SubscriptionData.Metadata["priceCents"], Is.EqualTo("1153"));
        Assert.That(created.SubscriptionData.Metadata["firstPriceCents"], Is.EqualTo("576"), "4.84 net + 19% VAT");
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That((request.Amount, request.RecurringAmount), Is.EqualTo((5.76m, (decimal?)11.53m)));
    }

    [Test]
    public async Task ManagedFirstPaymentOnlyCodeUsesAPercentageCouponOnTheFullNetPrice()
    {
        await AddOnceCode();

        await Controller("NO").StripeSubscribe("buyer", "l_premium", Options("NO", "INTRO"));

        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(969));
        Assert.That(coupons.Single(), Is.EqualTo(new StripeCouponSpec("coflnet_once_pct_5005", null, 50.05m, "eur")));
        Assert.That(created.Discounts.Single().Coupon, Is.EqualTo("coflnet_once_pct_5005"));
    }

    [Test]
    public async Task EveryRenewalCodeAndNoCodeCreateNoCoupon()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "ALWAYS", AmountType = "percent", Amount = 50 });
        await db.SaveChangesAsync();

        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "ALWAYS"));

        Assert.That(created.Discounts, Is.Null);
        Assert.That(coupons, Is.Empty);
    }

    [Test]
    public async Task DirectSessionCarriesTheInclusiveVatRateOfTheCountry()
    {
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(taxRates.Single(), Is.EqualTo(("DE", 1900)));
        Assert.That(created.LineItems.Single().TaxRates, Is.EqualTo(new[] { "txr_DE_1900" }));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1153), "the charged gross is unchanged");
    }

    [TestCase("NO", Description = "Managed Payments: Stripe taxes, tax rates are not allowed")]
    [TestCase("US", Description = "no VAT, no tax rate")]
    public async Task ManagedAndVatFreeSessionsCarryNoTaxRate(string country)
    {
        await Controller(country).StripeSubscribe("buyer", "l_premium", Options(country));

        Assert.That(taxRates, Is.Empty);
        Assert.That(created.LineItems.Single().TaxRates, Is.Null);
    }

    /// <summary>
    /// Regression: every non-direct country was sold Managed Payments, also where Stripe does not take over the tax.
    /// </summary>
    [TestCase("BR")]
    [TestCase("AE")]
    [TestCase("CL")]
    [TestCase("PE")]
    public async Task UncoveredCountriesGetNoStripeOrPayPalCheckout(string country)
    {
        var controller = Controller(country);

        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options(country)));
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options(country)));
        Assert.That((await controller.GetSubscriptionProviders(country)).Select(p => p.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
        await AddBlackFridayCode();
        Assert.That((await controller.GetAutoDiscount(country)).Result, Is.TypeOf<NoContentResult>());
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    /// <summary>
    /// Regression: the Managed Payments country is the buyer's unverified selection, so a buyer in an uncovered (BR) or
    /// uncovered (AE) country got a Managed Payments checkout by selecting a covered one (CH).
    /// </summary>
    [TestCase("BR")]
    [TestCase("AE")]
    public void ManagedPaymentsIsRefusedWhenTheIpCountryIsNotCovered(string ipCountry)
    {
        var error = Assert.ThrowsAsync<ApiException>(() => Controller(ipCountry).StripeSubscribe("buyer", "l_premium", Options("CH")));

        Assert.That(error.Message, Does.Contain(ipCountry));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    [TestCase("CH")]
    [TestCase("DE")]
    [TestCase(null, Description = "IP country unknown: Stripe locates the buyer itself")]
    public async Task ManagedPaymentsIsSoldWhenTheIpCountryIsCoveredOrUnknown(string ipCountry)
    {
        await Controller(ipCountry).StripeSubscribe("buyer", "l_premium", Options("CH"));

        stripe.Verify(s => s.CreateCheckoutSession(It.Is<SessionCreateOptions>(o => o.ManagedPayments.Enabled == true)), Times.Once);
    }

    [Test]
    public async Task UncoveredCountryHasNoProviderAtAllWithoutLemonSqueezy()
    {
        var controller = Controller("BR", new() { ["SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED"] = "false" });
        Assert.That(await controller.GetSubscriptionProviders("BR"), Is.Empty);
    }

    private static string Sha(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static SubscriptionDeclaration Declaration(string requestId = null) => new()
    {
        RequestId = requestId ?? Guid.NewGuid().ToString(), ImmediatePerformanceRequested = true,
        WithdrawalConsequenceAcknowledged = true, Locale = "de", DeclarationVersion = "v1", DeclarationText = "I agree",
        DeclarationSha256 = Sha("I agree"), AgreementId = "agreement", AgreementHash = Sha("agreement"),
        WithdrawalVersion = "w1", WithdrawalSha256 = Sha("withdrawal"), TaxCountry = "DE", VatRateBasisPoints = 1900,
        GrossEurCents = 1153, VatEurCents = 184
    };

    [TestCase("stripe")]
    [TestCase("paypal")]
    public async Task EarlyPerformanceDeclarationIsStoredWithTheCheckout(string provider)
    {
        var options = Options("DE");
        options.ServicePerformanceDeclaration = Declaration("3d7a6c4e-0d3a-4a43-9d2b-0f1d1d1c9b10");

        if (provider == "stripe")
            await Controller("DE").StripeSubscribe("buyer", "l_premium", options);
        else
            await Controller("DE").PayPalSubscribe("buyer", "l_premium", options);

        var stored = await db.ServicePerformanceDeclarations.SingleAsync();
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(stored.PaymentRequestId, Is.EqualTo(request.Id));
        Assert.That((stored.UserId, stored.ProductSlug, stored.DeclarationSha256, stored.Locale, stored.GrossEurCents),
            Is.EqualTo(("buyer", "l_premium", Sha("I agree"), "de", 1153L)));
        Assert.That(stored.EndsAtUtc - stored.StartsAtUtc, Is.EqualTo(TimeSpan.FromSeconds(FourWeeks)));
    }

    [Test]
    public async Task InvalidDeclarationIsRejectedBeforeAnyCheckoutIsCreated()
    {
        var options = Options("DE");
        options.ServicePerformanceDeclaration = Declaration();
        options.ServicePerformanceDeclaration.DeclarationSha256 = Sha("another text");

        var error = Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", options));

        Assert.That(error.Message, Does.Contain("invalid_service_performance_declaration"));
        Assert.That(await db.PaymentRequests.CountAsync(), Is.Zero);
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    /// <summary>The same switch as the prepaid flow: only enforced when LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION is set</summary>
    [Test]
    public async Task MissingDeclarationIsOnlyRefusedWhenEnforced()
    {
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));
        Assert.That(await db.ServicePerformanceDeclarations.CountAsync(), Is.Zero);

        var enforced = Controller("DE", new() { ["LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION"] = "true" });
        var error = Assert.ThrowsAsync<ApiException>(() => enforced.StripeSubscribe("buyer2", "l_premium", Options("DE")));
        Assert.That(error.Message, Does.Contain("service_performance_declaration_required"));
    }

    [Test]
    public async Task DeclarationIdOfAnotherUserIsRefused()
    {
        var id = Guid.NewGuid().ToString();
        var first = Options("DE");
        first.ServicePerformanceDeclaration = Declaration(id);
        await Controller("DE").StripeSubscribe("buyer", "l_premium", first);
        var second = Options("DE");
        second.ServicePerformanceDeclaration = Declaration(id);

        var error = Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("other", "l_premium", second));
        Assert.That(error.Message, Does.Contain("already used"));
    }

    /// <summary>
    /// Regression: a declaration id sent again with a later checkout was moved to it, also away from a paid subscription,
    /// which then had no declaration left.
    /// </summary>
    [Test]
    public async Task DeclarationOfAPaidCheckoutStaysWithIt()
    {
        var id = Guid.NewGuid().ToString();
        var first = Options("DE");
        first.ServicePerformanceDeclaration = Declaration(id);
        await Controller("DE").StripeSubscribe("buyer", "l_premium", first);
        var paid = await db.PaymentRequests.SingleAsync();
        paid.State = PaymentRequest.Status.PAID;
        await db.SaveChangesAsync();
        var second = Options("DE");
        second.ServicePerformanceDeclaration = Declaration(id);

        var error = Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", second));

        Assert.That(error.Message, Does.Contain("already used"));
        Assert.That((await db.ServicePerformanceDeclarations.SingleAsync()).PaymentRequestId, Is.EqualTo(paid.Id));
    }

    private async Task<TopUpOptions> BlackFridayWithCreator(string country)
    {
        await AddPlans();
        await AddBlackFridayCode();
        db.CreatorCodes.Add(new Coflnet.Payments.Models.CreatorCode { Code = "CREATOR", CreatorUserId = "creator", DiscountPercent = 10, IsActive = true });
        await db.SaveChangesAsync();
        var options = Options(country, "BF2026");
        options.CreatorCode = "CREATOR";
        // Like a request in production: nothing tracked, so the plan comes without its groups (ProductService does not load them)
        db.ChangeTracker.Clear();
        return options;
    }

    /// <summary>
    /// Owner decision (§ 309 Nr. 9 b BGB): a year continues every 4 weeks at the 4-week price of its tier. The creator code
    /// and the 4-week amount of the "forever" code apply to it: 9.69 - 10% = 8.72, -25% = 6.54, + 19% VAT = 7.78.
    /// </summary>
    [Test]
    public async Task StripeYearCheckoutStatesAndRecordsTheFourWeekRenewal()
    {
        await Controller("DE").StripeSubscribe("buyer", "l_premium_52", await BlackFridayWithCreator("DE"));

        var line = created.LineItems.Single().PriceData;
        Assert.That((line.UnitAmount, line.Recurring.IntervalCount), Is.EqualTo((5338L, 52L)), "99.69 - 10% = 89.72, -50% = 44.86, + VAT");
        Assert.That(created.SubscriptionData.Metadata["renewalProductSlug"], Is.EqualTo("l_premium"));
        Assert.That(created.SubscriptionData.Metadata["renewalPriceCents"], Is.EqualTo("778"));
        Assert.That(created.CustomText.Submit.Message, Does.StartWith("After the first 52 weeks it continues every 4 weeks at 7.78 EUR until cancelled."));
        Assert.That((await db.PaymentRequests.SingleAsync()).RecurringAmount, Is.EqualTo(7.78m), "what every later payment charges");
    }

    [Test]
    public async Task ManagedPaymentsYearRenewsAtTheNetFourWeekPrice()
    {
        await Controller("NO").StripeSubscribe("buyer", "l_premium_13", await BlackFridayWithCreator("NO"));

        Assert.That(created.ManagedPayments.Enabled, Is.True);
        Assert.That(created.SubscriptionData.Metadata["renewalPriceCents"], Is.EqualTo("654"), "net: Stripe adds the tax");
        Assert.That(created.CustomText.Submit.Message, Does.StartWith("After the first 13 weeks it continues every 4 weeks at 6.54 EUR"));
    }

    /// <summary>
    /// Regression: the renewal was priced from the year's copy of the code, whose amount already was the 52 week one, so a
    /// code {amount 0, periodAmounts {52: 50}} renewed at 50% off. It has no 4-week amount: the renewals pay its default 0%.
    /// </summary>
    [Test]
    public async Task RenewalUsesTheFourWeekAmountOfTheCodeNotTheYearsAmount()
    {
        await AddPlans();
        db.DiscountCodes.Add(new DiscountCode
        {
            Code = "HALF", AmountType = "percent", Amount = 0, SubscriptionOnly = true, PeriodAmounts = new() { [52] = 50 }
        });
        await db.SaveChangesAsync();

        await Controller("DE").StripeSubscribe("buyer", "l_premium_52", Options("DE", "HALF"));

        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(5931), "the year: 50% off");
        Assert.That(created.SubscriptionData.Metadata["renewalPriceCents"], Is.EqualTo("1153"), "the 4-week renewal: full price");
    }

    /// <summary>A "first payment only" code lowers the first year only; the 4-week renewals pay the full 4-week price</summary>
    [Test]
    public async Task FirstPaymentOnlyCodeDoesNotLowerTheFourWeekRenewal()
    {
        await AddPlans();
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();

        await Controller("DE").StripeSubscribe("buyer", "l_premium_52", Options("DE", "INTRO"));

        Assert.That(created.SubscriptionData.Metadata["firstPriceCents"], Is.EqualTo("5931"));
        Assert.That(created.SubscriptionData.Metadata["renewalPriceCents"], Is.EqualTo("1153"));
    }

    /// <summary>
    /// PayPal: the year is the TRIAL cycle of a plan whose REGULAR cycles last 4 weeks; the subscription prices them at the
    /// 4-week renewal price
    /// </summary>
    [Test]
    public async Task PayPalYearIsATrialCycleThenFourWeekCyclesAtTheRenewalPrice()
    {
        await Controller("DE").PayPalSubscribe("buyer", "l_premium_52", await BlackFridayWithCreator("DE"));

        var plan = paypalPlans.Single();
        Assert.That((plan.Weeks, plan.Price, plan.RenewalWeeks, plan.RenewalPrice), Is.EqualTo((52, 99.69m, (int?)4, (decimal?)9.69m)));
        Assert.That((paypalRequest.Price, paypalRequest.RecurringPrice), Is.EqualTo((53.38m, (decimal?)7.78m)));
        Assert.That((await db.PaymentRequests.SingleAsync()).RecurringAmount, Is.EqualTo(7.78m));
        Assert.That((await db.PayPalPlans.SingleAsync()).RenewalWeeks, Is.EqualTo(4));
    }

    /// <summary>
    /// A 13 or 52 week plan without a 4-week plan of its tier could not renew lawfully: it is not sold, and the code is not
    /// used up by the refused checkout
    /// </summary>
    [Test]
    public async Task LongPlanWithoutAFourWeekPlanIsNotSoldAndDoesNotUseTheCode()
    {
        db.TopUpProducts.Add(new TopUpProduct { Slug = "l_bazaarpro-year", Title = "year", Price = 99.69m, Cost = 1, CurrencyCode = "eur",
            OwnershipSeconds = FiftyTwoWeeks, Type = Product.ProductType.SERVICE });
        await AddBlackFridayCode();
        var controller = Controller("DE");
        created = null;

        Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_bazaarpro-year", Options("DE", "BF2026")));
        Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_bazaarpro-year", Options("DE", "BF2026")));

        Assert.That((await db.DiscountCodes.SingleAsync()).TimesRedeemed, Is.Zero);
        Assert.That(created, Is.Null);
    }

    /// <summary>
    /// Regression (review): a PayPal extension request (a new subscription starting when the buyer's current term ends) of the
    /// same plan and price was handed out as the open checkout of a new subscription, and counted toward the checkout limits
    /// </summary>
    [Test]
    public async Task PayPalExtensionRequestsAreNoOpenCheckouts()
    {
        var plan = await db.TopUpProducts.SingleAsync(p => p.Slug == "l_premium");
        var buyer = await users.GetOrCreate("buyer");
        for (var i = 0; i < MaxOpened; i++)
            db.PaymentRequests.Add(new PaymentRequest
            {
                User = buyer, ProductId = plan, Provider = "paypal", SessionId = $"I-EXT{i}", Amount = 11.53m, Country = "DE",
                State = PaymentRequest.Status.CREATED, CreatedAt = DateTime.UtcNow, ExtendsSubscriptionId = "I-OLD",
                CreateOnIp = System.Net.IPAddress.Parse("203.0.113.7")
            });
        await db.SaveChangesAsync();

        var response = await Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That((response.Id, response.DirectLink), Is.EqualTo(("I-SUB", "https://www.paypal.com/approve")));
        paypal.Verify(p => p.Get(It.IsAny<string>()), Times.Never, "no extension is reused as the checkout");
        Assert.That(await db.PaymentRequests.CountAsync(r => r.ExtendsSubscriptionId == null && r.SessionId == "I-SUB"), Is.EqualTo(1));
    }

    private async Task SeedAttempts(int count, TimeSpan age, string sessionId, string ip = "203.0.113.7")
    {
        var plan = await db.TopUpProducts.SingleAsync(p => p.Slug == "l_premium");
        var buyer = await users.GetOrCreate("buyer");
        for (var i = 0; i < count; i++)
            db.PaymentRequests.Add(new PaymentRequest
            {
                User = buyer, ProductId = plan, Provider = "stripe", SessionId = sessionId == null ? null : $"{sessionId}{i}",
                Amount = 1 + i, State = PaymentRequest.Status.CREATED, CreatedAt = DateTime.UtcNow - age,
                CreateOnIp = System.Net.IPAddress.Parse(ip)
            });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task RequestsWithoutAnOpenedCheckoutDoNotCountAndDoNotBlock()
    {
        await SeedAttempts(8, TimeSpan.FromMinutes(2), null);

        var response = await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(response.Id, Is.EqualTo("cs_1"));
    }

    [Test]
    public async Task FailedCheckoutCreationIsMarkedFailedAndNeverBlocksRetries()
    {
        stripe.Setup(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>())).ThrowsAsync(new InvalidOperationException("403"));
        var controller = Controller("DE");

        for (var i = 0; i < 8; i++)
            Assert.ThrowsAsync<ApiException>(() => controller.StripeSubscribe("buyer", "l_premium", Options("DE")));

        Assert.That(await db.PaymentRequests.AllAsync(r => r.State == PaymentRequest.Status.FAILED && r.SessionId == null));
    }

    [Test]
    public async Task FourOpenedCheckoutsInTenMinutesPassAndTheFifthIsRefused()
    {
        await SeedAttempts(MaxOpened - 1, TimeSpan.FromMinutes(2), "cs_old");
        var controller = Controller("DE");

        await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));
        var refused = Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options("DE")));

        Assert.That(refused.Message, Does.Contain("try again later"));
    }

    [Test]
    public async Task IdenticalRetryReturnsTheExistingCheckoutEvenAtTheShortWindowLimit()
    {
        var controller = Controller("DE");
        await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));
        await SeedAttempts(MaxOpened - 1, TimeSpan.FromMinutes(2), "cs_old");
        stripe.Setup(s => s.GetCheckoutSession("cs_1")).ReturnsAsync(new Session { Id = "cs_1", Url = "https://checkout.stripe.com/c/cs_1" });
        stripe.Invocations.Clear();

        var again = await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));

        Assert.That(again.Id, Is.EqualTo("cs_1"));
        stripe.Verify(s => s.CreateCheckoutSession(It.IsAny<SessionCreateOptions>()), Times.Never);
    }

    [Test]
    public async Task NineOpenedCheckoutsFromOneIpInTenHoursPassAndMoreNeedSupport()
    {
        await SeedAttempts(TopUpController.MaxOpenedCheckoutsFewIpsPer10Hours, TimeSpan.FromHours(2), "cs_old");
        var controller = Controller("DE");

        await controller.StripeSubscribe("buyer", "l_premium", Options("DE"));
        var refused = Assert.ThrowsAsync<ApiException>(() => controller.PayPalSubscribe("buyer", "l_premium", Options("DE")));

        Assert.That(refused.Message, Does.Contain("support"));
    }

    [Test]
    public async Task MoreThanTenOpenedCheckoutsFromManyIpsInTenHoursNeedSupport()
    {
        await SeedAttempts(4, TimeSpan.FromHours(2), "cs_a", "203.0.113.1");
        await SeedAttempts(4, TimeSpan.FromHours(2), "cs_b", "203.0.113.2");
        await SeedAttempts(3, TimeSpan.FromHours(2), "cs_c", "203.0.113.3");

        var refused = Assert.ThrowsAsync<ApiException>(() => Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE")));

        Assert.That(refused.Message, Does.Contain("support"));
    }

    private const int MaxOpened = TopUpController.MaxOpenedCheckoutsPer10Minutes;

    private static DiscountCode Auto(string code, DateTime? createdAt = null, DateTime? startsAt = null, DateTime? expiresAt = null,
        bool active = true, int? maxRedemptions = null, int redeemed = 0) => new()
    {
        Code = code, AmountType = "percent", Amount = 10, SubscriptionOnly = true, AutoApply = true, IsActive = active,
        CreatedAt = createdAt ?? DateTime.UtcNow, StartsAt = startsAt, ExpiresAt = expiresAt, MaxRedemptions = maxRedemptions,
        TimesRedeemed = redeemed, PeriodAmounts = new() { [13] = 50 }
    };
}
