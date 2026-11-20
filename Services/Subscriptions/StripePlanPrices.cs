using System;
using System.Collections.Generic;
using Stripe;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// A recurring price of a plan's Stripe product (<see cref="StripePlans.ProductId"/>)
/// </summary>
/// <param name="PlanSlug">The plan; its Stripe product identifies it once billed</param>
/// <param name="PlanName">Product name shown to the buyer (used when the product is created)</param>
/// <param name="UnitAmount">Cents per period: gross incl. VAT for direct subscriptions, net for Managed Payments</param>
/// <param name="Currency">Lower case ISO currency</param>
/// <param name="Weeks">The billing period in weeks</param>
/// <param name="TaxBehavior">"exclusive" for Managed Payments, null for direct sales (the amount is the final gross)</param>
public record StripePlanPriceSpec(string PlanSlug, string PlanName, long UnitAmount, string Currency, int Weeks, string TaxBehavior);

/// <summary>
/// Subscription item changes that may become pending updates (<c>pending_if_incomplete</c>) must name a Price: pending updates
/// support only <c>price</c>, <c>quantity</c> and <c>discounts</c> on items, not <c>price_data</c>
/// (docs.stripe.com/billing/subscriptions/pending-updates-reference). The prices are created once per plan, amount, currency,
/// period and tax behaviour and found again by a deterministic lookup key.
/// </summary>
public static class StripePlanPrices
{
    public static string LookupKey(StripePlanPriceSpec spec) =>
        $"coflnet_{spec.PlanSlug}_{spec.Weeks}w_{spec.UnitAmount}_{spec.Currency.ToLowerInvariant()}_{spec.TaxBehavior ?? "unspecified"}";

    /// <summary>Whether an existing price bills exactly the spec (a lookup key moved to another price elsewhere never applies)</summary>
    public static bool Matches(Price price, string productId, StripePlanPriceSpec spec) =>
        price != null && price.Active && price.ProductId == productId && price.UnitAmount == spec.UnitAmount
        && string.Equals(price.Currency, spec.Currency, StringComparison.OrdinalIgnoreCase)
        && price.Recurring?.Interval == "week" && price.Recurring.IntervalCount == spec.Weeks
        && (spec.TaxBehavior == null ? price.TaxBehavior is null or "unspecified" : price.TaxBehavior == spec.TaxBehavior);

    /// <summary>
    /// Stripe refused the price itself (archived/inactive, or gone): a cached id is stale and the price has to be looked up or created
    /// again
    /// </summary>
    public static bool IsUnusablePrice(StripeException e) =>
        e.HttpStatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound
        && (e.StripeError?.Param?.Contains("price", StringComparison.OrdinalIgnoreCase) == true
            || e.StripeError?.Message?.Contains("inactive", StringComparison.OrdinalIgnoreCase) == true
            || e.StripeError?.Message?.Contains("archived", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Runs <paramref name="update"/> with the plan's price; when Stripe refuses that price (<see cref="IsUnusablePrice"/>), the
    /// cached id is evicted and the update retried once with a price looked up or created again
    /// </summary>
    public static async System.Threading.Tasks.Task<T> WithUsablePrice<T>(Func<System.Threading.Tasks.Task<string>> ensurePrice,
        Action evict, Func<string, System.Threading.Tasks.Task<T>> update)
    {
        var price = await ensurePrice();
        try
        {
            return await update(price);
        }
        catch (StripeException e) when (IsUnusablePrice(e))
        {
            evict();
            return await update(await ensurePrice());
        }
    }

    /// <param name="transferLookupKey">Take the lookup key over from the price holding it (an archived or changed one)</param>
    public static PriceCreateOptions CreateOptions(StripePlanPriceSpec spec, string productId, bool transferLookupKey = false) => new()
    {
        Product = productId,
        UnitAmount = spec.UnitAmount,
        Currency = spec.Currency,
        TaxBehavior = spec.TaxBehavior,
        Recurring = new PriceRecurringOptions { Interval = "week", IntervalCount = spec.Weeks },
        LookupKey = LookupKey(spec),
        TransferLookupKey = transferLookupKey ? true : null,
        Metadata = new Dictionary<string, string> { [SubscriptionService.StripeMetadata.ProductSlug] = spec.PlanSlug }
    };

    /// <summary>
    /// A switch to a longer term: the new term's price, the billing cycle restarts now, the term is invoiced at once without
    /// proration credit, and the change only applies once that invoice is paid. A promotion is part of the price: a 13/52 week
    /// price is billed once (the renewal schedule follows the term), so it is the same as a one-time coupon.
    /// </summary>
    public static SubscriptionUpdateOptions TermSwitchOptions(StripeSubscriptionSnapshot current, string priceId) => new()
    {
        Items = [new SubscriptionItemOptions { Id = current.ItemId, Price = priceId }],
        BillingCycleAnchor = SubscriptionBillingCycleAnchor.Now,
        ProrationBehavior = "none",
        PaymentBehavior = "pending_if_incomplete",
        Expand = ["latest_invoice", "items.data.price"]
    };

    /// <summary>
    /// A plan switch: an upgrade invoices the proration now and only applies once that invoice is paid, also for delayed payment
    /// methods (error_if_incomplete would apply a still processing payment); a downgrade changes the next bill
    /// </summary>
    public static SubscriptionUpdateOptions PlanChangeOptions(StripeSubscriptionSnapshot current, string priceId, bool invoiceNow) => new()
    {
        Items = [new SubscriptionItemOptions { Id = current.ItemId, Price = priceId }],
        ProrationBehavior = invoiceNow ? "always_invoice" : "none",
        PaymentBehavior = invoiceNow ? "pending_if_incomplete" : "allow_incomplete",
        Expand = ["latest_invoice", "items.data.price"]
    };
}
