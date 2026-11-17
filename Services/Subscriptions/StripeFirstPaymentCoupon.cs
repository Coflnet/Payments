using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// A one-time ("duration once") Stripe coupon, identified by a deterministic id so the same discount is created once
/// and reused by every later checkout.
/// </summary>
/// <param name="Id">Stripe coupon id</param>
/// <param name="AmountOffCents">Fixed amount in <paramref name="Currency"/> (direct sales), null for percent coupons</param>
/// <param name="PercentOff">Percent with at most two decimals (Managed Payments), null for fixed amount coupons</param>
/// <param name="Currency">Lower case ISO currency</param>
public record StripeCouponSpec(string Id, long? AmountOffCents, decimal? PercentOff, string Currency);

/// <summary>
/// "First payment only" discount codes on Stripe: the recurring price of the session is the full price and a coupon that
/// applies to the first invoice only lowers the first payment, so Stripe's page shows "X today, then Y".
/// Direct sales use a fixed amount (the gross difference, exact to the cent); Managed Payments uses a percentage because
/// Adaptive Pricing may charge in the buyer's local currency, where a fixed EUR amount would not apply.
/// </summary>
public static class StripeFirstPaymentCoupon
{
    public const string IdPrefix = "coflnet_once_";

    /// <summary>
    /// The coupon that turns <paramref name="recurringCents"/> into <paramref name="firstCents"/> for the first invoice;
    /// null when the first payment is not discounted.
    /// </summary>
    public static StripeCouponSpec For(StripeCheckoutMode mode, long recurringCents, long firstCents, string currency)
    {
        if (firstCents >= recurringCents)
            return null;
        currency = currency.ToLowerInvariant();
        if (mode == StripeCheckoutMode.Direct)
            return new($"{IdPrefix}{recurringCents - firstCents}_{currency}", recurringCents - firstCents, null, currency);
        var percent = Math.Round((recurringCents - firstCents) * 100m / recurringCents, 2, MidpointRounding.AwayFromZero);
        return new($"{IdPrefix}pct_{(int)(percent * 100)}", null, percent, currency);
    }

    /// <summary>Id versions tried before giving up (the base id, then _v2, _v3, ...)</summary>
    public const int MaxVersions = 5;

    /// <summary>
    /// The id of version <paramref name="version"/> of a coupon: the plain id first, then "_v2", "_v3", ...
    /// </summary>
    public static string VersionedId(string id, int version) => version <= 1 ? id : $"{id}_v{version}";

    /// <summary>
    /// Why an existing Stripe coupon must not be reused for <paramref name="spec"/>, null when its terms match:
    /// duration once, the same fixed amount and currency (or percentage), and still valid. A coupon with other terms
    /// (edited in the dashboard, or created by hand under our id) would silently apply a wrong discount.
    /// </summary>
    public static string TermsMismatch(StripeCouponSpec spec, Stripe.Coupon existing)
    {
        if (existing.Duration != "once")
            return $"duration is {existing.Duration}, expected once";
        if (existing.Valid != true)
            return "it is no longer valid (redeemed or expired)";
        if (spec.AmountOffCents != null)
            return existing.AmountOff == spec.AmountOffCents && existing.PercentOff == null
                && string.Equals(existing.Currency, spec.Currency, StringComparison.OrdinalIgnoreCase)
                ? null : $"terms are amount_off {existing.AmountOff} {existing.Currency} / percent_off {existing.PercentOff}, expected amount_off {spec.AmountOffCents} {spec.Currency}";
        return existing.PercentOff == spec.PercentOff && existing.AmountOff == null
            ? null : $"terms are amount_off {existing.AmountOff} / percent_off {existing.PercentOff}, expected percent_off {spec.PercentOff}";
    }

    /// <summary>
    /// The id of a coupon with exactly the terms of <paramref name="spec"/>: the existing one when its terms match,
    /// otherwise (error logged) the next versioned id, created when missing. Never applies a coupon with other terms.
    /// </summary>
    /// <param name="get">Loads a coupon by id, null when it does not exist</param>
    /// <param name="create">Creates the coupon of the given spec (its id is the versioned one) and returns its id</param>
    public static async Task<string> Resolve(StripeCouponSpec spec, Func<string, Task<Stripe.Coupon>> get,
        Func<StripeCouponSpec, Task<string>> create, ILogger logger)
    {
        for (var version = 1; version <= MaxVersions; version++)
        {
            var versioned = spec with { Id = VersionedId(spec.Id, version) };
            var existing = await get(versioned.Id);
            if (existing == null)
                return await create(versioned);
            var mismatch = TermsMismatch(spec, existing);
            if (mismatch == null)
                return versioned.Id;
            logger?.LogError("Stripe coupon {CouponId} can not be reused: {Mismatch}; using {NextId} instead",
                versioned.Id, mismatch, VersionedId(spec.Id, version + 1));
        }
        throw new ApiException($"No Stripe coupon with the terms of {spec.Id} could be found or created.");
    }
}
