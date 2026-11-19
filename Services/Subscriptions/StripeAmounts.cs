using System;
using System.Collections.Generic;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Stripe amounts are integers in the currency's smallest unit (https://docs.stripe.com/currencies): cents for most
/// currencies, whole units for zero-decimal ones (JPY, KRW, ...) and thousandths for three-decimal ones (BHD, KWD, ...).
/// Managed Payments with Adaptive Pricing can charge any of them.
/// </summary>
public static class StripeAmounts
{
    /// <summary>Stripe's zero-decimal currencies. ISK and UGX are not among them: the API keeps them as two-decimal values.</summary>
    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "VND", "VUV", "XAF", "XOF", "XPF"
    };

    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "JOD", "KWD", "OMR", "TND"
    };

    /// <summary>A Stripe amount in major units of <paramref name="currency"/>, e.g. 1153 EUR = 11.53, 1500 JPY = 1500</summary>
    public static decimal FromMinorUnits(long amount, string currency) => amount / (decimal)MinorUnitsPerUnit(currency);

    private static int MinorUnitsPerUnit(string currency)
    {
        var code = currency?.Trim() ?? "";
        if (ZeroDecimal.Contains(code))
            return 1;
        return ThreeDecimal.Contains(code) ? 1000 : 100;
    }
}
