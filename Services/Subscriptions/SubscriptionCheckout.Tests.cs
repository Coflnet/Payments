using System.Collections.Generic;
using System.Linq;
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
        Assert.That(price.TaxBehavior, Is.EqualTo("inclusive"));
        Assert.That(price.UnitAmount, Is.EqualTo(2492));
        Assert.That(options.Mode, Is.EqualTo("subscription"));
        Assert.That(options.SubscriptionData.Metadata["mode"], Is.EqualTo("managed"));
    }

    [Test]
    public void ManagedTaxCodeAndBehaviorAreConfigurable()
    {
        var options = Build(StripeCheckoutMode.Managed, new Dictionary<string, string>
        {
            ["STRIPE:MANAGED_TAX_CODE"] = "txcd_10000000", ["STRIPE:MANAGED_TAX_BEHAVIOR"] = "exclusive"
        });
        Assert.That(options.LineItems.Single().PriceData.ProductData.TaxCode, Is.EqualTo("txcd_10000000"));
        Assert.That(options.LineItems.Single().PriceData.TaxBehavior, Is.EqualTo("exclusive"));
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
