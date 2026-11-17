using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
/// all other countries get Stripe Managed Payments, but only those Stripe covers for tax (SUBSCRIPTIONS:MANAGED_COUNTRIES);
/// SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES (default none) removes countries from Managed Payments for non-tax reasons (sanctions);
/// SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED, SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED, SUBSCRIPTIONS:PAYPAL_ENABLED (default false)
/// switch the new providers on; SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED (default true) keeps
/// Lemon Squeezy as the fallback where nothing else applies.
/// The US is only direct while <see cref="UsDirectSubscriptionCap"/> is not reached for the buyer's state (the region passed to
/// <see cref="Capture"/>; <see cref="RefreshAsync"/> keeps the counts current; an unknown region or count counts as reached);
/// the US is in the default managed list, so capped buyers get Stripe Managed Payments.
/// A direct country without a configured VAT rate (<see cref="SubscriptionTax"/>) is not sold to directly, so no
/// direct provider is offered for it: it would be undercharged.
/// </summary>
public class SubscriptionRouting
{
    /// <summary>EU-27, United Kingdom and United States</summary>
    public const string DefaultDirectCountries =
        "AT,BE,BG,HR,CY,CZ,DK,EE,FI,FR,DE,GR,HU,IE,IT,LV,LT,LU,MT,NL,PL,PT,RO,SK,SI,ES,SE,GB,US";

    /// <summary>
    /// Countries blocked for Stripe Managed Payments although Stripe covers their tax. Empty: the tax-only blocks of
    /// <see cref="SalesRestrictions.BlockedCountries"/> (TR, SA, KR, VN, MX, MD) do not apply to Managed Payments, where Stripe is merchant of
    /// record and pays the local tax; CH/LI (Swiss VAT) and Northern Ireland postcodes (BT) are direct-only restrictions too.
    /// Put countries here (SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES, comma separated) that must not be sold to by any seller, e.g. sanctions.
    /// </summary>
    public const string DefaultManagedBlockedCountries = "";

    /// <summary>
    /// Countries in which Managed Payments takes over the indirect tax (cross-border sales of digital products), as
    /// documented by Stripe on 2026-11-17: https://docs.stripe.com/payments/managed-payments/tax-compliance
    /// (EU-27, GB and US included; RS only for sellers not VAT registered there, which Coflnet is not).
    /// Everywhere else Stripe leaves the tax to the seller, so Managed Payments is not offered there.
    /// Override with SUBSCRIPTIONS:MANAGED_COUNTRIES (comma separated). The list is only narrowed by
    /// SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES (<see cref="DefaultManagedBlockedCountries"/>), not by the tax-only
    /// <see cref="SalesRestrictions.BlockedCountries"/> that applies where Coflnet is the seller.
    /// </summary>
    public const string DefaultManagedCountries =
        "CM,EG,GH,KE,NG,UG,ZA,ZM,ZW," // Africa
        + "AM,AU,AZ,BN,GE,HK,ID,IL,IN,JP,KG,KR,KW,KZ,LA,MO,MY,NP,NZ,PH,QA,SA,SG,TH,TJ,TR,TW,VN," // Asia-Pacific
        + "AL,BY,CH,GB,GI,IS,LI,MD,NO,RS,UA," // Europe
        + "AT,BE,BG,CY,CZ,DE,DK,EE,ES,FI,FR,GR,HR,HU,IE,IT,LT,LU,LV,MT,NL,PL,PT,RO,SE,SI,SK," // European Union
        + "BB,BM,KY,MX,VG," // Latin America and Caribbean
        + "CA,US"; // North America

    private readonly IReadOnlySet<string> directCountries;
    private readonly IReadOnlySet<string> managedCountries;
    private readonly IReadOnlySet<string> managedBlockedCountries;
    private readonly bool stripeDirect;
    private readonly bool stripeManaged;
    private readonly bool payPal;
    private readonly UsDirectSubscriptionCap usCap;

    /// <summary>
    /// VAT rates added on top of the net plan price for direct sales
    /// </summary>
    public SubscriptionTax Tax { get; }

    /// <summary>
    /// Whether new Lemon Squeezy subscription checkouts are allowed at all
    /// </summary>
    public bool LemonSqueezyCheckoutEnabled { get; }

    /// <param name="config">see the class remarks</param>
    /// <param name="usCap">Limits direct US sales (SUBSCRIPTIONS:US_DIRECT_LIMIT); null: no limit</param>
    public SubscriptionRouting(IConfiguration config, UsDirectSubscriptionCap usCap = null)
    {
        this.usCap = usCap;
        Tax = new SubscriptionTax(config);
        directCountries = ParseCountries(config?["SUBSCRIPTIONS:DIRECT_COUNTRIES"] ?? DefaultDirectCountries);
        managedCountries = ParseCountries(config?["SUBSCRIPTIONS:MANAGED_COUNTRIES"] ?? DefaultManagedCountries);
        managedBlockedCountries = ParseCountries(config?["SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES"] ?? DefaultManagedBlockedCountries);
        stripeDirect = Flag(config, "SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED", false);
        stripeManaged = Flag(config, "SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED", false);
        payPal = Flag(config, "SUBSCRIPTIONS:PAYPAL_ENABLED", false);
        LemonSqueezyCheckoutEnabled = Flag(config, "SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED", true);
    }

    /// <summary>
    /// Takes one snapshot of the routing state (the US cap of the buyer's state); a request routes with a single snapshot so a concurrent
    /// refresh of the cap cannot change the decision half way. Pass <paramref name="usCapReached"/> to fix the state.
    /// </summary>
    /// <param name="region">The buyer's state (see <see cref="UsDirectSubscriptionCap.NormalizeRegion"/>); only used for US buyers.
    /// Unknown counts as the cap reached (conservative), unless there is no cap at all.</param>
    public Snapshot Capture(string region = null, bool? usCapReached = null) =>
        new(this, usCapReached ?? usCap?.ReachedFor(region) == true);

    /// <summary>
    /// True when Coflnet sells directly (and handles VAT) to this country
    /// </summary>
    public bool IsDirectCountry(string country) => Capture().IsDirectCountry(country);

    /// <summary>
    /// Refreshes the cached state of the US direct subscription cap; call before routing a request.
    /// </summary>
    public Task RefreshAsync() => usCap?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>
    /// The Stripe mode for this country, null when Stripe subscriptions are unavailable there
    /// </summary>
    public StripeCheckoutMode? StripeMode(string country) => Capture().StripeMode(country);

    /// <summary>
    /// PayPal subscriptions are direct sales only
    /// </summary>
    public bool PayPalAvailable(string country) => Capture().PayPalAvailable(country);

    /// <summary>
    /// The providers a buyer from <paramref name="country"/> can start a new subscription with.
    /// Lemon Squeezy is only listed when no other provider applies.
    /// </summary>
    public IReadOnlyList<SubscriptionProviderOption> ProvidersFor(string country) => Capture().ProvidersFor(country);

    /// <summary>
    /// The direct providers (Stripe, PayPal) a buyer from <paramref name="country"/> can use; without a country
    /// every enabled direct provider. Local discount codes only work with these, never with Lemon Squeezy.
    /// </summary>
    public IReadOnlyList<string> DirectProvidersFor(string country) => Capture().DirectProvidersFor(country);

    /// <summary>
    /// True when a new Lemon Squeezy subscription is allowed for this buyer
    /// </summary>
    public bool AllowsLemonSqueezyCheckout(string country) => Capture().AllowsLemonSqueezyCheckout(country);

    /// <summary>
    /// The routing decisions of one request, all made with the same state of the US cap.
    /// </summary>
    public sealed class Snapshot
    {
        private readonly SubscriptionRouting routing;
        private readonly bool usCapReached;

        internal Snapshot(SubscriptionRouting routing, bool usCapReached)
        {
            this.routing = routing;
            this.usCapReached = usCapReached;
        }

        /// <inheritdoc cref="SubscriptionRouting.IsDirectCountry"/>
        public bool IsDirectCountry(string country) => Normalize(country) is string c && IsDirect(c);

        /// <summary>
        /// Direct country, except the US once <see cref="UsDirectSubscriptionCap"/> is reached: those buyers go to Managed Payments.
        /// </summary>
        private bool IsDirect(string country) =>
            routing.directCountries.Contains(country) && !(country == UsDirectSubscriptionCap.Country && usCapReached);

        /// <inheritdoc cref="SubscriptionRouting.StripeMode"/>
        public StripeCheckoutMode? StripeMode(string country)
        {
            var c = Normalize(country);
            if (c == null)
                return null;
            if (IsDirect(c))
                return routing.stripeDirect && routing.SellsDirectlyTo(c) ? StripeCheckoutMode.Direct : null;
            return routing.stripeManaged && routing.SellsManagedTo(c) ? StripeCheckoutMode.Managed : null;
        }

        /// <inheritdoc cref="SubscriptionRouting.PayPalAvailable"/>
        public bool PayPalAvailable(string country) =>
            routing.payPal && Normalize(country) is string c && IsDirect(c) && routing.SellsDirectlyTo(c);

        /// <inheritdoc cref="SubscriptionRouting.ProvidersFor"/>
        public IReadOnlyList<SubscriptionProviderOption> ProvidersFor(string country)
        {
            var options = new List<SubscriptionProviderOption>();
            if (StripeMode(country) is StripeCheckoutMode mode)
                options.Add(new(SubscriptionProviders.Stripe, mode == StripeCheckoutMode.Direct ? "direct" : "managed"));
            if (PayPalAvailable(country))
                options.Add(new(SubscriptionProviders.PayPal, "direct"));
            if (options.Count == 0 && routing.LemonSqueezyCheckoutEnabled)
                options.Add(new(SubscriptionProviders.LemonSqueezy, "merchant_of_record"));
            return options;
        }

        /// <inheritdoc cref="SubscriptionRouting.DirectProvidersFor"/>
        public IReadOnlyList<string> DirectProvidersFor(string country)
        {
            if (Normalize(country) != null)
                return ProvidersFor(country).Select(p => p.Provider).Where(p => p != SubscriptionProviders.LemonSqueezy).ToList();
            var providers = new List<string>();
            if (routing.stripeDirect || routing.stripeManaged)
                providers.Add(SubscriptionProviders.Stripe);
            if (routing.payPal)
                providers.Add(SubscriptionProviders.PayPal);
            return providers;
        }

        /// <inheritdoc cref="SubscriptionRouting.AllowsLemonSqueezyCheckout"/>
        public bool AllowsLemonSqueezyCheckout(string country) =>
            routing.LemonSqueezyCheckoutEnabled && (Normalize(country) == null
                || ProvidersFor(country).Any(p => p.Provider == SubscriptionProviders.LemonSqueezy));
    }

    /// <summary>
    /// Direct countries that are offered no direct provider because their VAT rate is missing or invalid;
    /// empty while no direct provider is switched on.
    /// </summary>
    public IReadOnlyList<string> DirectCountriesWithoutVatRate() => stripeDirect || payPal
        ? directCountries.Where(c => !Tax.HasRate(c)).Order(StringComparer.Ordinal).ToList()
        : [];

    /// <summary>
    /// True when a Managed Payments sale to a buyer located in <paramref name="country"/> is allowed: Stripe covers the tax
    /// there and it is not blocked for Managed Payments (whatever country the buyer selected)
    /// </summary>
    public bool ManagedCovers(string country) => Normalize(country) is string c && SellsManagedTo(c);

    /// <summary>
    /// Stripe covers the tax there and it is not blocked for non-tax reasons. The tax-only
    /// <see cref="SalesRestrictions.BlockedCountries"/> do not matter: Stripe is the seller.
    /// </summary>
    private bool SellsManagedTo(string country) =>
        managedCountries.Contains(country) && !managedBlockedCountries.Contains(country);

    private bool SellsDirectlyTo(string country) =>
        !SalesRestrictions.IsCountryBlocked(country) && !SalesRestrictions.IsBlockedForDirectProviders(country)
        && Tax.HasRate(country);

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
