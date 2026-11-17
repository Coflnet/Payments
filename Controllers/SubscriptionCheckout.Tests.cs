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

    [SetUp]
    public void SetUpGateways()
    {
        paypalPlans.Clear();
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
    public async Task FirstPaymentOnlyCodeDiscountsOnlyThePayPalTrialCycle()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "INTRO", AmountType = "percent", Amount = 50, Duration = DiscountCode.FirstPaymentOnly });
        await db.SaveChangesAsync();
        await Controller("DE").PayPalSubscribe("buyer", "l_premium", Options("DE", "INTRO"));
        Assert.That(paypalRequest.Price, Is.EqualTo(4.84m));
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(9.69m));
        var request = await db.PaymentRequests.SingleAsync();
        Assert.That(request.Amount, Is.EqualTo(4.84m));
        Assert.That(request.RecurringAmount, Is.EqualTo(9.69m));
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
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(872));
        Assert.That(await db.PaymentRequests.Select(r => r.Amount).ToListAsync(), Is.EquivalentTo(new[] { 9.69m, 8.72m }));
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
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(4.84m));
        Assert.That(await db.PaymentRequests.Select(r => r.RecurringAmount).ToListAsync(), Is.EquivalentTo(new decimal?[] { 9.69m, null }));
    }

    /// <summary>
    /// Regression: a promotion checkout session stayed payable for Stripe's default 24 hours after the code expired.
    /// </summary>
    [TestCase("DE")]
    [TestCase("BR")]
    public async Task StripeSessionExpiresWithItsDiscountCode(string country)
    {
        var expires = DateTime.UtcNow.AddHours(2);
        db.DiscountCodes.Add(new DiscountCode { Code = "PROMO", AmountType = "percent", Amount = 25, ExpiresAt = expires });
        await db.SaveChangesAsync();

        await Controller(country).StripeSubscribe("buyer", "l_premium", Options(country, "PROMO"));

        Assert.That(created.ExpiresAt, Is.EqualTo(expires).Within(TimeSpan.FromSeconds(1)));
        Assert.That((await db.PaymentRequests.SingleAsync()).ExpiresAt, Is.EqualTo(expires).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task StripeSessionWithoutAnExpiringCodeKeepsTheDefaultExpiry()
    {
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE"));
        Assert.That(created.ExpiresAt, Is.Null);
    }

    [TestCase(5, 31)]       // expires sooner than Stripe's 30 minute minimum
    [TestCase(120, 120)]    // within the allowed range: the code's expiry
    [TestCase(-60, 31)]     // already expired (validated a moment ago): the minimum
    public void SessionExpiryIsClampedToStripesRange(int codeMinutes, int expectedMinutes)
    {
        var now = new DateTime(2026, 11, 30, 23, 0, 0, DateTimeKind.Utc);
        Assert.That(StripeSubscriptionCheckoutBuilder.SessionExpiry(now.AddMinutes(codeMinutes), now), Is.EqualTo(now.AddMinutes(expectedMinutes)));
    }

    [Test]
    public void SessionExpiryIsLeftToStripeWhenTheCodeOutlivesTheSession()
    {
        var now = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        Assert.That(StripeSubscriptionCheckoutBuilder.SessionExpiry(now.AddDays(3), now), Is.Null);
        Assert.That(StripeSubscriptionCheckoutBuilder.SessionExpiry(null, now), Is.Null);
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
        Assert.ThrowsAsync<ApiException>(() => Controller("BR").PayPalSubscribe("buyer", "l_premium", Options("BR")));
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
        Assert.That((await controller.GetSubscriptionProviders("BR")).Single().Mode, Is.EqualTo("managed"));
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

    [TestCase("l_premium", 727)]
    [TestCase("l_premium_13", 1484)]
    [TestCase("l_premium_52", 4984)]
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
        await Controller("BR").StripeSubscribe("buyer", "l_premium", Options("BR", "BF2026"));
        Assert.That(created.ManagedPayments.Enabled, Is.True);
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(727));
    }

    [Test]
    public async Task PeriodWithoutAnEntryUsesTheDefaultAmount()
    {
        db.DiscountCodes.Add(new DiscountCode { Code = "ONLY13", AmountType = "percent", Amount = 10, PeriodAmounts = new() { [13] = 50 } });
        await db.SaveChangesAsync();
        await Controller("DE").StripeSubscribe("buyer", "l_premium", Options("DE", "ONLY13"));
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(872));
    }

    [Test]
    public async Task PeriodAmountAppliesToPayPalPrices()
    {
        await AddPlans();
        await AddBlackFridayCode();
        await Controller("DE").PayPalSubscribe("buyer", "l_premium_13", Options("DE", "BF2026"));
        Assert.That(paypalRequest.Price, Is.EqualTo(14.84m));
        Assert.That(paypalRequest.RecurringPrice, Is.EqualTo(14.84m));
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
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(2274));
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
        Assert.That(paypalRequest.Price, Is.EqualTo(25.27m));
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
        Assert.That(created.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(727));
        Assert.That((await db.DiscountCodes.AsNoTracking().SingleAsync()).TimesRedeemed, Is.EqualTo(1));
    }

    private static DiscountCode Auto(string code, DateTime? createdAt = null, DateTime? startsAt = null, DateTime? expiresAt = null,
        bool active = true, int? maxRedemptions = null, int redeemed = 0) => new()
    {
        Code = code, AmountType = "percent", Amount = 10, SubscriptionOnly = true, AutoApply = true, IsActive = active,
        CreatedAt = createdAt ?? DateTime.UtcNow, StartsAt = startsAt, ExpiresAt = expiresAt, MaxRedemptions = maxRedemptions,
        TimesRedeemed = redeemed, PeriodAmounts = new() { [13] = 50 }
    };
}
