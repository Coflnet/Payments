using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Single source of truth for the countries we do not sell to. Used by the
    /// traditional payment providers (coin sales) and by Expert Config quotes
    /// so both cannot drift apart.
    /// </summary>
    public static class SalesRestrictions
    {
        /// <summary>
        /// Countries where registering for taxes as a foreign seller is too much overhead
        /// (AE: can't register for taxes as a foreigner).
        /// </summary>
        public static readonly IReadOnlySet<string> BlockedCountries = new HashSet<string>(StringComparer.Ordinal)
        {
            "TR", "AE", "SA", "KR", "VN", "CL", "MX", "PE", "MD"
        };

        /// <summary>
        /// Countries blocked for providers where Coflnet itself is the seller (Stripe, PayPal, CoinGate).
        /// Coflnet GmbH is not registered for Swiss VAT (MWST) and foreign sellers of electronic services
        /// must register once worldwide turnover reaches CHF 100,000, so CH (and LI, which shares the Swiss
        /// VAT area) is blocked. LemonSqueezy is merchant of record and handles Swiss VAT, so it never
        /// consults this list. Override with config SALES:DIRECT_PROVIDER_BLOCKED_COUNTRIES
        /// (comma separated, empty lifts it for Stripe and PayPal, e.g. after registering).
        /// CoinGate additionally has its own allow-list (DoWeAcceptCoinGateFrom) that must be extended too.
        /// </summary>
        public static IReadOnlySet<string> DirectProviderBlockedCountries { get; private set; } = ParseCountries(null);

        public const string ConfigKey = "SALES:DIRECT_PROVIDER_BLOCKED_COUNTRIES";

        public static void Configure(string commaSeparatedCountries)
            => DirectProviderBlockedCountries = ParseCountries(commaSeparatedCountries);

        private static IReadOnlySet<string> ParseCountries(string csv)
            => (csv ?? "CH,LI")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// True if direct providers (not LemonSqueezy) must not sell to this country.
        /// </summary>
        public static bool IsBlockedForDirectProviders(string country)
            => country != null && DirectProviderBlockedCountries.Contains(country.Trim().ToUpperInvariant());

        /// <summary>
        /// Message for customers of blocked countries.
        /// </summary>
        public static string DirectProviderBlockedMessage(string country)
            => $"We can not sell via this payment method to your country ({country}). Please pay with LemonSqueezy instead.";

        /// <summary>
        /// True if the country is blocked regardless of the postal code.
        /// </summary>
        public static bool IsCountryBlocked(string country)
            => country != null && BlockedCountries.Contains(country);

        /// <summary>
        /// Validates if we sell to the given country / postal code combination via a direct provider
        /// (Stripe/PayPal callbacks). Not used for LemonSqueezy.
        /// </summary>
        public static bool DoWeSellTo(string country, string postalCode)
        {
            if (country == "GB" && (postalCode?.StartsWith("BT") ?? false))
                return false; // registration too complicated for northern ireland
            return !IsCountryBlocked(country) && !IsBlockedForDirectProviders(country);
        }
    }
}
