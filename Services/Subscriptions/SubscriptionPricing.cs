using System;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Price of a direct subscription. Like one-time Stripe/PayPal purchases the plan price is the
/// VAT-inclusive gross price; creator codes are applied first, then local discount codes.
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
    /// Minor units of a two decimal currency
    /// </summary>
    public static long ToCents(decimal price) => (long)Math.Round(price * 100, MidpointRounding.AwayFromZero);
}
