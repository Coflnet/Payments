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
    [TestCase("BR", StripeCheckoutMode.Managed)]
    [TestCase("CH", StripeCheckoutMode.Managed)]
    [TestCase("TR", StripeCheckoutMode.Managed)]
    public void CountriesOutsideTheDirectListUseManagedPayments(string country, StripeCheckoutMode mode)
    {
        Assert.That(Routing().StripeMode(country), Is.EqualTo(mode));
        Assert.That(Routing().PayPalAvailable(country), Is.EqualTo(mode == StripeCheckoutMode.Direct));
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
        foreach (var country in new[] { "DE", "BR", null })
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
        Assert.That(routing.ProvidersFor("BR").Select(p => p.Provider), Is.EqualTo(new[] { "lemonsqueezy" }));
        Assert.That(routing.AllowsLemonSqueezyCheckout("BR"), Is.True);
        Assert.That(Routing().ProvidersFor("BR").Select(p => p.Provider + ":" + p.Mode), Is.EqualTo(new[] { "stripe:managed" }));
    }

    [Test]
    public void LemonSqueezyCheckoutCanBeSwitchedOff()
    {
        var routing = new SubscriptionRouting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED"] = "false"
        }).Build());
        Assert.That(routing.ProvidersFor("BR"), Is.Empty);
        Assert.That(routing.AllowsLemonSqueezyCheckout(null), Is.False);
    }
}
