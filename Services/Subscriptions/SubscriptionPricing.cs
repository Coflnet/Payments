using System;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Price of a subscription. Plan prices are NET (the Lemon Squeezy era pricing): creator codes are applied first,
/// then local discount codes, and the buyer pays that net price plus the VAT of the billing country.
/// <see cref="GrossCents(long, int)"/> is the single place the gross is computed; every amount this service
/// sends to or expects from Stripe/PayPal (checkout, recurring price, plan switches, amount checks) goes through it.
/// Stripe Managed Payments is the exception: the net price is sent with tax_behavior "exclusive" and Stripe adds the tax.
/// </summary>
public static class SubscriptionPricing
{
    /// <summary>Stripe's minimum charge for EUR</summary>
    public const long MinimumCents = 50;

    /// <summary>
    /// Applies the creator and discount codes to the plan's list price
    /// </summary>
    public static decimal Apply(decimal listPrice, CreatorCode creatorCode, ValidatedDiscount discount)
    {
        var price = listPrice;
        if (creatorCode != null)
            price -= Math.Round(price * creatorCode.DiscountPercent / 100, 2);
        if (discount?.IsValid == true)
            price = discount.CalculateDiscountedPrice(price);
        return price;
    }

    /// <summary>
    /// The net price of every 4-week renewal of a 13 or 52 week subscription (<see cref="SubscriptionRenewal"/>): the 4-week
    /// list price with the creator code (a lasting discount) and a "forever" discount code with its 4-week amount
    /// (periodAmounts["4"], else amount). A "first payment only" code does not apply to renewals.
    /// <paramref name="discount"/> must be the code as validated, not a <see cref="ValidatedDiscount.ForPeriod"/> copy of
    /// another period (that copy's amount is the other period's).
    /// </summary>
    public static decimal RenewalNet(decimal renewalListPrice, CreatorCode creatorCode, ValidatedDiscount discount)
    {
        var code = discount?.Duration == DiscountCode.FirstPaymentOnly ? null : discount?.ForPeriod(SubscriptionRenewal.RenewalWeeks);
        return Apply(renewalListPrice, creatorCode, code);
    }

    /// <summary>
    /// Net price (after codes) plus VAT, in cents: net cents are rounded first (<see cref="ToCents"/>), then
    /// gross = round-half-away-from-zero(net * (10000 + bp) / 10000).
    /// </summary>
    public static long GrossCents(decimal netPrice, int vatBasisPoints) => GrossCents(ToCents(netPrice), vatBasisPoints);

    public static long GrossCents(long netCents, int vatBasisPoints) =>
        (long)Math.Round(netCents * (10_000m + vatBasisPoints) / 10_000m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The VAT contained in a gross amount (gross minus the net recovered with the same rounding as TransactionService)
    /// </summary>
    public static long VatPortionCents(long grossCents, int vatBasisPoints) =>
        grossCents - (long)Math.Round(grossCents * 10_000m / (10_000 + vatBasisPoints), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Minor units of a two decimal currency
    /// </summary>
    public static long ToCents(decimal price) => (long)Math.Round(price * 100, MidpointRounding.AwayFromZero);
}
