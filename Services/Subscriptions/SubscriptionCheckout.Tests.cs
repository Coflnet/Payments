using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Stripe.Checkout;

namespace Coflnet.Payments.Services.Subscriptions;

public class SubscriptionCheckoutTests
{
    /// <summary>Parameters Stripe rejects on Managed Payments sessions</summary>
    private static readonly string[] ManagedPaymentsForbidden =
    [
        "adaptive_pricing", "automatic_tax", "tax_id_collection", "subscription_data.default_tax_rates",
        "payment_method_configuration", "payment_method_types", "customer_update", "shipping_address_collection",
        "shipping_options", "subscription_data.application_fee_percent", "subscription_data.on_behalf_of",
        "subscription_data.transfer_data", "subscription_data.invoice_settings", "invoice_creation",
        "billing_address_collection", "payment_method_options"
    ];

    private static readonly TopUpProduct Plan = new()
    {
        Id = 7, Slug = "l_premium-quarter", Title = "3 months Premium", Description = "renewing", Price = 27.69m,
        Cost = 5400, CurrencyCode = "eur", OwnershipSeconds = 7776000, Type = Product.ProductType.SERVICE
    };

    private static SessionCreateOptions Build(StripeCheckoutMode mode, IDictionary<string, string> config = null) =>
        StripeSubscriptionCheckoutBuilder.Build(
            new StripeSubscriptionCheckout(new User { ExternalId = "buyer" }, Plan, 2492, 13, mode, "BR",
                "https://ok", "https://cancel", "a@b.c", "SAVE10", null, 42),
            StripeManagedSettings.From(new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string>()).Build()));

    private static JObject Params(SessionCreateOptions options) =>
        JObject.Parse(JsonConvert.SerializeObject(options, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

    private static IEnumerable<string> Paths(JToken token) => token switch
    {
        JObject o => o.Properties().SelectMany(p => new[] { p.Name }.Concat(Paths(p.Value).Select(c => p.Name + "." + c))),
        JArray a => a.SelectMany(Paths),
        _ => []
    };

    [Test]
    public void ManagedPaymentsSessionOnlyUsesAcceptedParameters()
    {
        var options = Build(StripeCheckoutMode.Managed);
        var paths = Paths(Params(options)).ToList();

        Assert.That(options.ManagedPayments?.Enabled, Is.True);
        Assert.That(paths.Intersect(ManagedPaymentsForbidden), Is.Empty);
        var price = options.LineItems.Single().PriceData;
        Assert.That(price.ProductData.TaxCode, Is.EqualTo("txcd_10103000"));
        Assert.That(price.TaxBehavior, Is.EqualTo("exclusive"), "Managed Payments: net amount, Stripe adds the tax");
        Assert.That(price.UnitAmount, Is.EqualTo(2492));
        Assert.That(options.Mode, Is.EqualTo("subscription"));
        Assert.That(options.SubscriptionData.Metadata["mode"], Is.EqualTo("managed"));
    }

    /// <summary>Stripe's list of Checkout Session parameters not allowed with managed_payments[enabled]=true</summary>
    private static readonly string[] ManagedPaymentsDisallowed =
    [
        "automatic_tax", "tax_id_collection", "subscription_data.default_tax_rates", "payment_method_collection",
        "payment_method_configuration", "payment_method_options", "payment_method_types", "saved_payment_method_options",
        "customer_update.name", "customer_update.address", "shipping_address_collection", "shipping_options",
        "subscription_data.application_fee_percent", "subscription_data.on_behalf_of", "subscription_data.transfer_data",
        "subscription_data.invoice_settings"
    ];

    /// <summary>
    /// The form body Stripe.net sends (including empty nested option objects) holds none of the disallowed parameters,
    /// for a session with every optional input set.
    /// </summary>
    [Test]
    public async Task ManagedPaymentsRequestBodyHasNoDisallowedParameter()
    {
        var options = StripeSubscriptionCheckoutBuilder.Build(
            new StripeSubscriptionCheckout(new User { ExternalId = "buyer" }, Plan, 2492, 13, StripeCheckoutMode.Managed, "BR",
                "https://ok", "https://cancel", "a@b.c", "SAVE10", "creator", 42),
            StripeManagedSettings.From(new ConfigurationBuilder().Build()));

        var sent = await SentToStripe(options);
        var keys = sent.Body.Split('&')
            .Select(pair => Uri.UnescapeDataString(pair.Split('=')[0]).Replace("][", ".").Replace("[", ".").TrimEnd(']'))
            .ToList();

        Assert.That(string.CompareOrdinal(sent.ApiVersion, "2025-03-31.basil"), Is.GreaterThanOrEqualTo(0), sent.ApiVersion);
        Assert.That(keys, Does.Contain("managed_payments.enabled"));
        foreach (var disallowed in ManagedPaymentsDisallowed)
            Assert.That(keys.Where(k => k == disallowed || k.StartsWith(disallowed + ".")), Is.Empty, disallowed);
        Assert.That(new object[]
        {
            options.AutomaticTax, options.TaxIdCollection, options.SubscriptionData.DefaultTaxRates, options.PaymentMethodCollection,
            options.PaymentMethodConfiguration, options.PaymentMethodOptions, options.PaymentMethodTypes,
            options.SavedPaymentMethodOptions, options.CustomerUpdate, options.ShippingAddressCollection, options.ShippingOptions,
            options.SubscriptionData.ApplicationFeePercent, options.SubscriptionData.OnBehalfOf,
            options.SubscriptionData.TransferData, options.SubscriptionData.InvoiceSettings
        }, Is.All.Null);
    }

    /// <summary>
    /// Direct buyers always pay EUR: the wire body carries adaptive_pricing[enabled]=false, Managed Payments sessions
    /// (which reject the parameter) do not carry it.
    /// </summary>
    [Test]
    public async Task DirectSessionsDisableAdaptivePricingAndManagedOnesDoNotSendIt()
    {
        var direct = StripeSubscriptionCheckoutBuilder.Build(
            new StripeSubscriptionCheckout(new User { ExternalId = "buyer" }, Plan, 2492, 13, StripeCheckoutMode.Direct, "DE",
                "https://ok", "https://cancel", "a@b.c", null, null, 42),
            StripeManagedSettings.From(new ConfigurationBuilder().Build()));

        var sent = await SentToStripe(direct);

        Assert.That(Uri.UnescapeDataString(sent.Body), Does.Contain("adaptive_pricing[enabled]=false"));
        Assert.That(Build(StripeCheckoutMode.Managed).AdaptivePricing, Is.Null);
    }

    /// <summary>
    /// Creates the session through Stripe.net against a fake HTTP handler and returns the form body and API version
    /// exactly as they would go over the wire.
    /// </summary>
    private static async Task<(string Body, string ApiVersion)> SentToStripe(SessionCreateOptions options)
    {
        var handler = new CapturingHandler();
        var client = new Stripe.StripeClient("sk_test_123", httpClient: new Stripe.SystemNetHttpClient(new HttpClient(handler)));
        await new SessionService(client).CreateAsync(options);
        return (handler.Body, handler.ApiVersion);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string Body { get; private set; }
        public string ApiVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ApiVersion = request.Headers.GetValues("Stripe-Version").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"cs_test","object":"checkout.session"}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private static StripeSubscriptionCheckout Checkout(StripeCheckoutMode mode, long unit = 1153, long? first = null,
        string coupon = null, string taxRate = null) =>
        new(new User { ExternalId = "buyer" }, Plan, unit, 13, mode, "DE", "https://ok", "https://cancel", "a@b.c", "SAVE10", null, 42,
            first, coupon, taxRate);

    private static SessionCreateOptions Build(StripeSubscriptionCheckout checkout) =>
        StripeSubscriptionCheckoutBuilder.Build(checkout, StripeManagedSettings.From(new ConfigurationBuilder().Build()));

    [Test]
    public void FirstPaymentCouponIsOneTimeAndTheRecurringPriceStaysFull()
    {
        var options = Build(Checkout(StripeCheckoutMode.Direct, 1153, 576, "coflnet_once_577_eur"));

        Assert.That(options.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1153), "Stripe shows this as the renewal price");
        Assert.That(options.Discounts.Single().Coupon, Is.EqualTo("coflnet_once_577_eur"));
        Assert.That(options.SubscriptionData.Metadata["priceCents"], Is.EqualTo("1153"));
        Assert.That(options.SubscriptionData.Metadata["firstPriceCents"], Is.EqualTo("576"));
    }

    [Test]
    public void SessionsWithoutAFirstPaymentCodeHaveNoCouponOrFirstPrice()
    {
        var options = Build(Checkout(StripeCheckoutMode.Direct, 1153, 1153));
        Assert.That(options.Discounts, Is.Null);
        Assert.That(options.SubscriptionData.Metadata.ContainsKey("firstPriceCents"), Is.False);
    }

    [Test]
    public void FirstPaymentCouponIsAllowedOnManagedPaymentsSessions()
    {
        var options = Build(Checkout(StripeCheckoutMode.Managed, 2492, 1246, "coflnet_once_pct_5000"));
        var paths = Paths(Params(options)).ToList();

        Assert.That(options.Discounts.Single().Coupon, Is.EqualTo("coflnet_once_pct_5000"));
        Assert.That(paths.Intersect(ManagedPaymentsDisallowed.Concat(ManagedPaymentsForbidden)), Is.Empty);
    }

    [TestCase(StripeCheckoutMode.Direct, 1153L, 576L, "coflnet_once_577_eur", 577L, null)]
    [TestCase(StripeCheckoutMode.Managed, 969L, 485L, "coflnet_once_pct_4995", null, 49.95)]
    [TestCase(StripeCheckoutMode.Managed, 2492L, 1869L, "coflnet_once_pct_2500", null, 25.0)]
    public void FirstPaymentCouponsHaveDeterministicIds(StripeCheckoutMode mode, long recurring, long first, string id, long? amount, double? percent)
    {
        var coupon = StripeFirstPaymentCoupon.For(mode, recurring, first, "EUR");
        Assert.That((coupon.Id, coupon.AmountOffCents, coupon.PercentOff), Is.EqualTo((id, amount, (decimal?)(percent == null ? null : (decimal)percent))));
        Assert.That(coupon.Currency, Is.EqualTo("eur"));
        Assert.That(StripeFirstPaymentCoupon.For(mode, recurring, first, "eur"), Is.EqualTo(coupon));
    }

    [Test]
    public void NoCouponWhenTheFirstPaymentIsNotDiscounted() =>
        Assert.That(StripeFirstPaymentCoupon.For(StripeCheckoutMode.Direct, 1153, 1153, "eur"), Is.Null);

    private static readonly StripeCouponSpec FixedCoupon = new("coflnet_once_200_eur", 200, null, "eur");

    private static Stripe.Coupon Coupon(string duration = "once", long? amountOff = 200, decimal? percentOff = null,
        string currency = "eur", bool valid = true) =>
        new() { Duration = duration, AmountOff = amountOff, PercentOff = percentOff, Currency = currency, Valid = valid };

    private static async Task<(string id, List<string> created, List<string> errors)> Resolve(StripeCouponSpec spec,
        Dictionary<string, Stripe.Coupon> existing)
    {
        var created = new List<string>();
        var logger = new ListLogger();
        var id = await StripeFirstPaymentCoupon.Resolve(spec, key => Task.FromResult(existing.GetValueOrDefault(key)),
            versioned => { created.Add(versioned.Id); return Task.FromResult(versioned.Id); }, logger);
        return (id, created, logger.Errors);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Errors { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }

    [Test]
    public async Task ACouponWithMatchingTermsIsReusedWithoutCreatingOne()
    {
        var (id, created, errors) = await Resolve(FixedCoupon, new() { [FixedCoupon.Id] = Coupon() });

        Assert.That(id, Is.EqualTo(FixedCoupon.Id));
        Assert.That(created, Is.Empty);
        Assert.That(errors, Is.Empty);
    }

    [Test]
    public async Task AMissingCouponIsCreatedUnderTheBaseId()
    {
        var (id, created, _) = await Resolve(FixedCoupon, new());

        Assert.That(id, Is.EqualTo(FixedCoupon.Id));
        Assert.That(created, Is.EqualTo(new[] { FixedCoupon.Id }));
    }

    /// <summary>
    /// Regression: a coupon found by id was applied without looking at its terms, so one edited or created by hand
    /// under our id gave a wrong discount. Every wrong term logs an error and moves to the _v2 id.
    /// </summary>
    [TestCase("forever", 200L, null, "eur", true, TestName = "WrongDuration")]
    [TestCase("once", 100L, null, "eur", true, TestName = "WrongAmount")]
    [TestCase("once", 200L, null, "usd", true, TestName = "WrongCurrency")]
    [TestCase("once", null, 10, "eur", true, TestName = "PercentCouponForAFixedSpec")]
    [TestCase("once", 200L, null, "eur", false, TestName = "InvalidCoupon")]
    public async Task ACouponWithOtherTermsIsNeverReusedAndAVersionedIdIsUsed(string duration, long? amount, int? percent, string currency, bool valid)
    {
        var existing = new Dictionary<string, Stripe.Coupon> { [FixedCoupon.Id] = Coupon(duration, amount, percent, currency, valid) };

        var (id, created, errors) = await Resolve(FixedCoupon, existing);

        Assert.That(id, Is.EqualTo(FixedCoupon.Id + "_v2"));
        Assert.That(created, Is.EqualTo(new[] { FixedCoupon.Id + "_v2" }));
        Assert.That(errors, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AVersionedCouponWithMatchingTermsIsReused()
    {
        var (id, created, _) = await Resolve(FixedCoupon, new()
        {
            [FixedCoupon.Id] = Coupon(amountOff: 999), [FixedCoupon.Id + "_v2"] = Coupon()
        });

        Assert.That(id, Is.EqualTo(FixedCoupon.Id + "_v2"));
        Assert.That(created, Is.Empty);
    }

    [Test]
    public async Task APercentCouponIsComparedByPercentage()
    {
        var spec = new StripeCouponSpec("coflnet_once_pct_2500", null, 25m, "eur");

        Assert.That((await Resolve(spec, new() { [spec.Id] = Coupon(amountOff: null, percentOff: 25m) })).created, Is.Empty);
        Assert.That((await Resolve(spec, new() { [spec.Id] = Coupon(amountOff: null, percentOff: 20m) })).id, Is.EqualTo(spec.Id + "_v2"));
    }

    /// <summary>
    /// Both modes tell the buyer the renewal terms next to the button (Managed Payments adds the reseller notice)
    /// and use the "pay" button label.
    /// </summary>
    [Test]
    public void SubmitTextStatesTheRenewalTermsAndTheResellerForManagedPayments()
    {
        var direct = Build(Checkout(StripeCheckoutMode.Direct, 1153, taxRate: "txr_de"));
        var managed = Build(Checkout(StripeCheckoutMode.Managed, 969));

        Assert.That(direct.CustomText.Submit.Message, Does.StartWith("Renews every 13 weeks at 11.53 EUR until cancelled."));
        Assert.That(direct.CustomText.Submit.Message, Does.Contain("sky.coflnet.com/account").And.Contain("includes VAT"));
        Assert.That(direct.CustomText.Submit.Message, Does.Not.Contain("reseller"));
        Assert.That(managed.CustomText.Submit.Message, Does.Contain("9.69 EUR").And.Contain("Sold through Link (Stripe's reseller), which charges any applicable taxes."));
        // regression: Stripe refuses submit_type "pay" in subscription mode
        Assert.That(new[] { direct.SubmitType, managed.SubmitType }, Is.All.Null);
        Assert.That(direct.CustomText.Submit.Message.Length, Is.LessThan(StripeSubscriptionDisclosure.MaxLength));
    }

    /// <summary>The account url of each product line: BazaarPro's billing settings, SkyCofl's account, or SUBSCRIPTIONS:ACCOUNT_URL:{line}</summary>
    [Test]
    public void AccountUrlFollowsTheProductLine()
    {
        var bazaarPro = new TopUpProduct { Slug = "l_bazaarpro-year", Groups = [new Group { Slug = "premium" }] };
        var premium = new TopUpProduct { Slug = "l_premium", Groups = [new Group { Slug = "premium" }] };
        var overridden = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            [StripeSubscriptionDisclosure.AccountUrlConfigPrefix + "default"] = "coflnet.example/account"
        }).Build();

        Assert.That(StripeSubscriptionDisclosure.AccountUrlFor(bazaarPro, null), Is.EqualTo("pro.skyblock.bz/dashboard/settings/billing"));
        Assert.That(StripeSubscriptionDisclosure.AccountUrlFor(premium, null), Is.EqualTo("sky.coflnet.com/account"));
        Assert.That(StripeSubscriptionDisclosure.AccountUrlFor(premium, overridden), Is.EqualTo("coflnet.example/account"));
        Assert.That(StripeSubscriptionDisclosure.AccountUrlFor(bazaarPro, overridden), Is.EqualTo("pro.skyblock.bz/dashboard/settings/billing"));
        var message = Build(Checkout(StripeCheckoutMode.Direct, 969) with { AccountUrl = "pro.skyblock.bz/dashboard/settings/billing" })
            .CustomText.Submit.Message;
        Assert.That(message, Does.EndWith("Cancel anytime in your account at pro.skyblock.bz/dashboard/settings/billing."));
    }

    /// <summary>
    /// Regression: a direct US sale (VAT rate 0, no VAT charged) was told "The price includes VAT."
    /// </summary>
    [Test]
    public void DirectSaleWithoutVatDoesNotClaimToIncludeVat()
    {
        var message = Build(Checkout(StripeCheckoutMode.Direct, 969)).CustomText.Submit.Message;

        Assert.That(message, Does.StartWith("Renews every 13 weeks at 9.69 EUR until cancelled."));
        Assert.That(message, Does.Not.Contain("VAT"));
    }

    /// <summary>
    /// Direct sessions attach the inclusive VAT rate to the gross price line (receipts show rate and amount, the amount is
    /// unchanged); Managed Payments does not allow tax rates, Stripe taxes there.
    /// </summary>
    [Test]
    public void InclusiveVatRateIsOnlyAttachedToDirectSessions()
    {
        var direct = Build(Checkout(StripeCheckoutMode.Direct, 1153, taxRate: "txr_de"));
        var managed = Build(Checkout(StripeCheckoutMode.Managed, 969, taxRate: "txr_de"));

        Assert.That(direct.LineItems.Single().TaxRates, Is.EqualTo(new[] { "txr_de" }));
        Assert.That(direct.LineItems.Single().PriceData.UnitAmount, Is.EqualTo(1153));
        Assert.That(managed.LineItems.Single().TaxRates, Is.Null);
    }

    [Test]
    public async Task ManagedSessionWithCouponAndTextSendsThemAndNoDisallowedParameter()
    {
        var sent = await SentToStripe(Build(Checkout(StripeCheckoutMode.Managed, 2492, 1246, "coflnet_once_pct_5000")));
        var pairs = sent.Body.Split('&').Select(p => Uri.UnescapeDataString(p.Replace('+', ' '))).ToList();

        Assert.That(pairs, Does.Contain("discounts[0][coupon]=coflnet_once_pct_5000"));
        Assert.That(pairs.Any(p => p.StartsWith("submit_type=")), Is.False, "Stripe refuses submit_type pay in subscription mode");
        Assert.That(pairs.Any(p => p.StartsWith("custom_text[submit][message]=Renews every 13 weeks")), Is.True);
        Assert.That(pairs, Does.Contain("managed_payments[enabled]=true"));
    }

    [Test]
    public void ManagedTaxCodeAndBehaviorAreConfigurable()
    {
        var options = Build(StripeCheckoutMode.Managed, new Dictionary<string, string>
        {
            ["STRIPE:MANAGED_TAX_CODE"] = "txcd_10000000", ["STRIPE:MANAGED_TAX_BEHAVIOR"] = "inclusive"
        });
        Assert.That(options.LineItems.Single().PriceData.ProductData.TaxCode, Is.EqualTo("txcd_10000000"));
        Assert.That(options.LineItems.Single().PriceData.TaxBehavior, Is.EqualTo("inclusive"));
    }

    [Test]
    public void DirectSessionCollectsTheBillingAddressAndIsNotManaged()
    {
        var options = Build(StripeCheckoutMode.Direct);
        var price = options.LineItems.Single().PriceData;
        Assert.That(options.ManagedPayments, Is.Null);
        Assert.That(options.BillingAddressCollection, Is.EqualTo("required"));
        Assert.That(price.ProductData.TaxCode, Is.Null);
        Assert.That(price.Recurring.Interval, Is.EqualTo("week"));
        Assert.That(price.Recurring.IntervalCount, Is.EqualTo(13));
        Assert.That(price.Currency, Is.EqualTo("eur"));
        Assert.That(options.ClientReferenceId, Is.EqualTo("buyer"));
        var metadata = options.SubscriptionData.Metadata;
        Assert.That(metadata["userId"], Is.EqualTo("buyer"));
        Assert.That(metadata["productSlug"], Is.EqualTo("l_premium-quarter"));
        Assert.That(metadata["priceCents"], Is.EqualTo("2492"));
        Assert.That(metadata["discountCode"], Is.EqualTo("SAVE10"));
        Assert.That(metadata.ContainsKey("creatorCode"), Is.False);
    }

    [TestCase(969, 1900, 1153)]   // 9.69 * 1.19 = 11.5311
    [TestCase(969, 2000, 1163)]   // 11.628
    [TestCase(969, 0, 969)]
    [TestCase(485, 1900, 577)]    // 5.7715
    [TestCase(5, 2550, 6)]        // 6.2775
    [TestCase(1000, 2050, 1205)]  // exactly .5 rounds away from zero
    public void GrossIsTheNetCentsPlusVatRoundedHalfAwayFromZero(long net, int basisPoints, long gross) =>
        Assert.That(SubscriptionPricing.GrossCents(net, basisPoints), Is.EqualTo(gross));

    [Test]
    public void NetPriceIsRoundedToCentsBeforeVatIsAdded() =>
        Assert.That(SubscriptionPricing.GrossCents(4.845m, 1900), Is.EqualTo(SubscriptionPricing.GrossCents(485, 1900)));

    [Test]
    public void VatPortionIsWhatGrossExceedsItsNet()
    {
        Assert.That(SubscriptionPricing.VatPortionCents(1153, 1900), Is.EqualTo(184));
        Assert.That(SubscriptionPricing.VatPortionCents(969, 0), Is.Zero);
    }

    [Test]
    public void OnlyTheUsDefaultsToNoVat()
    {
        var tax = new SubscriptionTax(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            [SubscriptionTax.RateKeyPrefix + "DE"] = "1900", [SubscriptionTax.RateKeyPrefix + "XX"] = "20000"
        }).Build());
        Assert.That(tax.RateFor("de"), Is.EqualTo(1900));
        Assert.That(tax.RateFor("US"), Is.Zero);
        Assert.That(tax.RateFor("FR"), Is.Null);
        Assert.That(tax.RateFor("XX"), Is.Null, "out of range");
        Assert.That(tax.RateFor(null), Is.Null);
    }

    /// <summary>
    /// Regression: a rate written as a percentage ("25.5") made the config binder throw on every provider lookup of
    /// that country; it is a missing rate instead (the country gets no direct provider and startup logs it).
    /// </summary>
    [TestCase("25.5")]
    [TestCase("-100")]
    [TestCase("19%")]
    public void InvalidRateIsAMissingRate(string value)
    {
        var routing = Routing(new Dictionary<string, string> { [SubscriptionTax.RateKeyPrefix + "FI"] = value });
        Assert.That(routing.Tax.RateFor("FI"), Is.Null);
        Assert.That(routing.StripeMode("FI"), Is.Null);
        Assert.That(routing.DirectCountriesWithoutVatRate(), Does.Contain("FI"));
    }

    [Test]
    public void StartupReportsDirectCountriesWithoutARateOnlyWhileADirectProviderIsOn()
    {
        Assert.That(Routing().DirectCountriesWithoutVatRate(), Does.Contain("FR").And.Not.Contain("DE").And.Not.Contain("US"));
        var managedOnly = Routing(new Dictionary<string, string>
        {
            ["SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED"] = "false", ["SUBSCRIPTIONS:PAYPAL_ENABLED"] = "false"
        });
        Assert.That(managedOnly.DirectCountriesWithoutVatRate(), Is.Empty);
    }

    [TestCase(2430000, 4)]
    [TestCase(2419200, 4)]
    [TestCase(7776000, 13)]
    [TestCase(31536000, 52)]
    [TestCase(86400, null)]
    public void PlanPeriodsMapToWeeks(long seconds, int? weeks) =>
        Assert.That(SubscriptionService.IntervalWeeks(seconds), Is.EqualTo(weeks));

    private static SubscriptionRouting Routing(Dictionary<string, string> settings = null)
    {
        var values = new Dictionary<string, string>
        {
            [SubscriptionTax.RateKeyPrefix + "DE"] = "1900", [SubscriptionTax.RateKeyPrefix + "AT"] = "2000",
            [SubscriptionTax.RateKeyPrefix + "GB"] = "2000", [SubscriptionTax.RateKeyPrefix + "US"] = "0",
            ["SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED"] = "true",
            ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "true",
            ["SUBSCRIPTIONS:PAYPAL_ENABLED"] = "true"
        };
        foreach (var setting in settings ?? new Dictionary<string, string>())
            values[setting.Key] = setting.Value;
        return new SubscriptionRouting(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [TestCase("DE", StripeCheckoutMode.Direct)]
    [TestCase("gb", StripeCheckoutMode.Direct)]
    [TestCase("US", StripeCheckoutMode.Direct)]
    [TestCase("NO", StripeCheckoutMode.Managed)]
    [TestCase("CH", StripeCheckoutMode.Managed)]
    [TestCase("AU", StripeCheckoutMode.Managed)]
    public void CountriesOutsideTheDirectListUseManagedPayments(string country, StripeCheckoutMode mode)
    {
        Assert.That(Routing().StripeMode(country), Is.EqualTo(mode));
        Assert.That(Routing().PayPalAvailable(country), Is.EqualTo(mode == StripeCheckoutMode.Direct));
    }

    /// <summary>
    /// Regression: every country outside the direct list got Managed Payments, also where Stripe does not take over the
    /// tax (AE, CL, PE, BR, AR, CO).
    /// </summary>
    [TestCase("AE")]
    [TestCase("CL")]
    [TestCase("PE")]
    [TestCase("BR")]
    [TestCase("AR")]
    [TestCase("CO")]
    public void ManagedPaymentsIsNotOfferedWhereStripeDoesNotCoverTheTax(string country)
    {
        var routing = Routing();
        Assert.That(routing.StripeMode(country), Is.Null);
        Assert.That(routing.ManagedCovers(country), Is.False);
        Assert.That(routing.ProvidersFor(country).Select(p => p.Provider), Is.Empty.Or.EqualTo(new[] { "lemonsqueezy" }));
        Assert.That(routing.DirectProvidersFor(country), Is.Empty, "discount/auto and the provider list read this");
    }

    /// <summary>
    /// Regression: TR, SA, KR, VN, MX and MD are in <see cref="SalesRestrictions.BlockedCountries"/> purely for tax reasons
    /// (Coflnet would owe the local tax) but Stripe covers them: they get Managed Payments only, never a direct
    /// Stripe/PayPal checkout, even when listed as direct countries.
    /// </summary>
    [TestCase("TR")]
    [TestCase("SA")]
    [TestCase("KR")]
    [TestCase("VN")]
    [TestCase("MX")]
    [TestCase("MD")]
    public void TaxBlockedCountriesCoveredByStripeGetManagedPaymentsButNeverDirect(string country)
    {
        var routing = Routing();
        Assert.That(routing.StripeMode(country), Is.EqualTo(StripeCheckoutMode.Managed));
        Assert.That(routing.ManagedCovers(country), Is.True);
        Assert.That(routing.PayPalAvailable(country), Is.False);
        Assert.That(routing.ProvidersFor(country).Select(p => (p.Provider, p.Mode)), Is.EqualTo(new[] { ("stripe", "managed") }));
        Assert.That(routing.DirectProvidersFor(country), Is.EqualTo(new[] { "stripe" }));
        Assert.That(SalesRestrictions.IsCountryBlocked(country) && !SalesRestrictions.DoWeSellTo(country, null), Is.True,
            "one-time sales and direct invoices still refuse it");
        var listedDirect = Routing(new() { ["SUBSCRIPTIONS:DIRECT_COUNTRIES"] = "DE," + country });
        Assert.That(listedDirect.StripeMode(country), Is.Null);
        Assert.That(listedDirect.PayPalAvailable(country), Is.False);
    }

    [Test]
    public void BelarusIsBlockedForManagedPaymentsByDefault()
    {
        Assert.That(Routing().StripeMode("BY"), Is.Null);
        Assert.That(Routing().PayPalAvailable("BY"), Is.False);
        Assert.That(Routing().StripeMode("UA"), Is.EqualTo(StripeCheckoutMode.Managed));
        Assert.That(Routing(new() { ["SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES"] = "" }).StripeMode("BY"), Is.EqualTo(StripeCheckoutMode.Managed),
            "the config replaces the default");
    }

    [Test]
    public void ManagedBlockedCountriesAreConfigurableAndSeparateFromTheTaxBlocks()
    {
        var routing = Routing(new() { ["SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES"] = "tr, NO" });

        Assert.That(routing.StripeMode("TR"), Is.Null);
        Assert.That(routing.StripeMode("NO"), Is.Null);
        Assert.That(routing.StripeMode("SA"), Is.EqualTo(StripeCheckoutMode.Managed));
        Assert.That(routing.ManagedCovers("TR"), Is.False);
    }

    [Test]
    public void ManagedPaymentsStillCoversCountriesStripeDocumentsAndIsConfigurable()
    {
        Assert.That(new[] { "CH", "LI", "NO", "AU", "JP", "CA", "IN", "ZA" }.Select(c => Routing().StripeMode(c)),
            Is.All.EqualTo(StripeCheckoutMode.Managed));
        var narrowed = Routing(new() { ["SUBSCRIPTIONS:MANAGED_COUNTRIES"] = "NO" });
        Assert.That(narrowed.StripeMode("NO"), Is.EqualTo(StripeCheckoutMode.Managed));
        Assert.That(narrowed.StripeMode("AU"), Is.Null);
        Assert.That(narrowed.ProvidersFor("AU").Select(p => p.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
    }

    [Test]
    public void DirectCountriesAreConfigurable()
    {
        var routing = Routing(new() { ["SUBSCRIPTIONS:DIRECT_COUNTRIES"] = "de, at" });
        Assert.That(routing.StripeMode("AT"), Is.EqualTo(StripeCheckoutMode.Direct));
        Assert.That(routing.StripeMode("US"), Is.EqualTo(StripeCheckoutMode.Managed));
    }

    [Test]
    public void DirectListedButBlockedCountryGetsNoDirectProvider()
    {
        var routing = Routing(new() { ["SUBSCRIPTIONS:DIRECT_COUNTRIES"] = "DE,CH" });
        Assert.That(routing.StripeMode("CH"), Is.Null);
        Assert.That(routing.PayPalAvailable("CH"), Is.False);
    }

    [Test]
    public void DefaultsKeepLemonSqueezyEverywhereUntilProvidersAreEnabled()
    {
        var routing = new SubscriptionRouting(new ConfigurationBuilder().Build());
        foreach (var country in new[] { "DE", "BR", "NO", null })
        {
            Assert.That(routing.ProvidersFor(country).Select(p => p.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
            Assert.That(routing.AllowsLemonSqueezyCheckout(country), Is.True);
        }
    }

    [Test]
    public void LemonSqueezyIsOnlyTheFallback()
    {
        var routing = Routing(new() { ["SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED"] = "false" });
        Assert.That(routing.ProvidersFor("DE").Select(p => p.Provider + ":" + p.Mode), Is.EqualTo(new[] { "stripe:direct", "paypal:direct" }));
        Assert.That(routing.AllowsLemonSqueezyCheckout("DE"), Is.False);
        Assert.That(routing.ProvidersFor("NO").Select(p => p.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
        Assert.That(routing.AllowsLemonSqueezyCheckout("NO"), Is.True);
        Assert.That(Routing().ProvidersFor("NO").Select(p => p.Provider + ":" + p.Mode), Is.EqualTo(new[] { "stripe:managed" }));
    }

    [Test]
    public void LemonSqueezyCheckoutCanBeSwitchedOff()
    {
        var routing = new SubscriptionRouting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED"] = "false"
        }).Build());
        Assert.That(routing.ProvidersFor("NO"), Is.Empty);
        Assert.That(routing.AllowsLemonSqueezyCheckout(null), Is.False);
    }
}
