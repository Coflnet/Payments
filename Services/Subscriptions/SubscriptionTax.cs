using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// VAT rates (basis points) for direct subscription sales. Reads the same configuration as the expert marketplace
/// quotes in TransactionService: EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:{ISO country}.
/// A direct country without a rate can not be sold to (it would be undercharged); only the US defaults to 0
/// because one-time Stripe/PayPal purchases do not collect US sales tax either.
/// </summary>
public class SubscriptionTax
{
    public const string RateKeyPrefix = "EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:";
    private readonly IConfiguration config;

    public SubscriptionTax(IConfiguration config)
    {
        this.config = config;
    }

    /// <summary>
    /// The VAT rate of <paramref name="country"/> in basis points, null when none is configured (and it is not the US)
    /// or the configured value is no whole number of basis points between 0 and 10000 (e.g. "25.5" instead of "2550").
    /// </summary>
    public int? RateFor(string country)
    {
        var code = country?.Trim().ToUpperInvariant();
        if (code?.Length != 2)
            return null;
        var value = config?[RateKeyPrefix + code];
        if (value == null)
            return code == "US" ? 0 : null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) && rate <= 10_000 ? rate : null;
    }

    public bool HasRate(string country) => RateFor(country) != null;
}
