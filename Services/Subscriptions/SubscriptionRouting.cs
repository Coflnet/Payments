using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// How a Stripe subscription checkout is sold
/// </summary>
public enum StripeCheckoutMode
{
    /// <summary>Coflnet is the seller and handles VAT (EU, UK, US by default)</summary>
    Direct,
    /// <summary>Stripe Managed Payments: Stripe (Link) is merchant of record and remits the tax</summary>
    Managed
}

/// <summary>
/// A subscription provider offered to a buyer
/// </summary>
/// <param name="Provider">Provider slug, see <see cref="SubscriptionProviders"/></param>
/// <param name="Mode">"direct", "managed" or "merchant_of_record" (Lemon Squeezy)</param>
public record SubscriptionProviderOption(string Provider, string Mode);

/// <summary>
/// Single place deciding which subscription providers apply to a buyer country.
/// Config:
/// SUBSCRIPTIONS:DIRECT_COUNTRIES (comma separated, default EU-27 + GB + US) get direct Stripe/PayPal,
/// all other countries get Stripe Managed Payments;
/// SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED, SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED, SUBSCRIPTIONS:PAYPAL_ENABLED (default false)
/// switch the new providers on; SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED (default true) keeps
/// Lemon Squeezy as the fallback where nothing else applies.
/// </summary>
public class SubscriptionRouting
{
    /// <summary>EU-27, United Kingdom and United States</summary>
    public const string DefaultDirectCountries =
        "AT,BE,BG,HR,CY,CZ,DK,EE,FI,FR,DE,GR,HU,IE,IT,LV,LT,LU,MT,NL,PL,PT,RO,SK,SI,ES,SE,GB,US";

    private readonly IReadOnlySet<string> directCountries;
    private readonly bool stripeDirect;
    private readonly bool stripeManaged;
    private readonly bool payPal;

    /// <summary>
    /// Whether new Lemon Squeezy subscription checkouts are allowed at all
    /// </summary>
    public bool LemonSqueezyCheckoutEnabled { get; }

    public SubscriptionRouting(IConfiguration config)
    {
        directCountries = ParseCountries(config?["SUBSCRIPTIONS:DIRECT_COUNTRIES"] ?? DefaultDirectCountries);
        stripeDirect = Flag(config, "SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED", false);
        stripeManaged = Flag(config, "SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED", false);
        payPal = Flag(config, "SUBSCRIPTIONS:PAYPAL_ENABLED", false);
        LemonSqueezyCheckoutEnabled = Flag(config, "SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED", true);
    }

    /// <summary>
    /// True when Coflnet sells directly (and handles VAT) to this country
    /// </summary>
    public bool IsDirectCountry(string country) => Normalize(country) is string c && directCountries.Contains(c);

    /// <summary>
    /// The Stripe mode for this country, null when Stripe subscriptions are unavailable there
    /// </summary>
    public StripeCheckoutMode? StripeMode(string country)
    {
        var c = Normalize(country);
        if (c == null)
            return null;
        if (directCountries.Contains(c))
            return stripeDirect && SellsDirectlyTo(c) ? StripeCheckoutMode.Direct : null;
        return stripeManaged ? StripeCheckoutMode.Managed : null;
    }

    /// <summary>
    /// PayPal subscriptions are direct sales only
    /// </summary>
    public bool PayPalAvailable(string country) =>
        payPal && Normalize(country) is string c && directCountries.Contains(c) && SellsDirectlyTo(c);

    /// <summary>
    /// The providers a buyer from <paramref name="country"/> can start a new subscription with.
    /// Lemon Squeezy is only listed when no other provider applies.
    /// </summary>
    public IReadOnlyList<SubscriptionProviderOption> ProvidersFor(string country)
    {
        var options = new List<SubscriptionProviderOption>();
        if (StripeMode(country) is StripeCheckoutMode mode)
            options.Add(new(SubscriptionProviders.Stripe, mode == StripeCheckoutMode.Direct ? "direct" : "managed"));
        if (PayPalAvailable(country))
            options.Add(new(SubscriptionProviders.PayPal, "direct"));
        if (options.Count == 0 && LemonSqueezyCheckoutEnabled)
            options.Add(new(SubscriptionProviders.LemonSqueezy, "merchant_of_record"));
        return options;
    }

    /// <summary>
    /// The direct providers (Stripe, PayPal) a buyer from <paramref name="country"/> can use; without a country
    /// every enabled direct provider. Local discount codes only work with these, never with Lemon Squeezy.
    /// </summary>
    public IReadOnlyList<string> DirectProvidersFor(string country)
    {
        if (Normalize(country) != null)
            return ProvidersFor(country).Select(p => p.Provider).Where(p => p != SubscriptionProviders.LemonSqueezy).ToList();
        var providers = new List<string>();
        if (stripeDirect || stripeManaged)
            providers.Add(SubscriptionProviders.Stripe);
        if (payPal)
            providers.Add(SubscriptionProviders.PayPal);
        return providers;
    }

    /// <summary>
    /// True when a new Lemon Squeezy subscription is allowed for this buyer
    /// </summary>
    public bool AllowsLemonSqueezyCheckout(string country) =>
        LemonSqueezyCheckoutEnabled && (Normalize(country) == null
            || ProvidersFor(country).Any(p => p.Provider == SubscriptionProviders.LemonSqueezy));

    private static bool SellsDirectlyTo(string country) =>
        !SalesRestrictions.IsCountryBlocked(country) && !SalesRestrictions.IsBlockedForDirectProviders(country);

    private static string Normalize(string country)
    {
        var c = country?.Trim().ToUpperInvariant();
        return c?.Length == 2 ? c : null;
    }

    private static bool Flag(IConfiguration config, string key, bool fallback) =>
        bool.TryParse(config?[key], out var value) ? value : fallback;

    private static IReadOnlySet<string> ParseCountries(string csv) => csv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToUpperInvariant())
        .ToHashSet(StringComparer.Ordinal);
}
