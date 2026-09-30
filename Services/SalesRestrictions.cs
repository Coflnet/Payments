using System;
using System.Collections.Generic;

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
        /// True if the country is blocked regardless of the postal code.
        /// </summary>
        public static bool IsCountryBlocked(string country)
            => country != null && BlockedCountries.Contains(country);

        /// <summary>
        /// Validates if we sell to the given country / postal code combination.
        /// </summary>
        public static bool DoWeSellTo(string country, string postalCode)
        {
            if (country == "GB" && (postalCode?.StartsWith("BT") ?? false))
                return false; // registration too complicated for northern ireland
            return !IsCountryBlocked(country);
        }
    }
}
