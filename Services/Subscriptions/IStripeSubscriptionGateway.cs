using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Stripe.Checkout;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The Stripe Billing calls the subscription code needs, reduced to plain records so they can be faked in tests.
/// </summary>
public interface IStripeSubscriptionGateway
{
    /// <summary>Creates a hosted checkout session</summary>
    Task<Session> CreateCheckoutSession(SessionCreateOptions options);
    /// <summary>Loads an existing checkout session (to reuse its URL)</summary>
    Task<Session> GetCheckoutSession(string sessionId);
    /// <summary>Loads the current subscription state</summary>
    Task<StripeSubscriptionSnapshot> GetSubscription(string subscriptionId);
    /// <summary>Sets or clears cancel_at_period_end</summary>
    Task<StripeSubscriptionSnapshot> SetCancelAtPeriodEnd(string subscriptionId, bool cancel);
    /// <summary>Ends the subscription immediately, without a refund</summary>
    Task<StripeSubscriptionSnapshot> CancelNow(string subscriptionId);
    /// <summary>Swaps the price (and plan product) of the single subscription item</summary>
    Task<StripePriceChange> ChangePrice(StripeSubscriptionSnapshot current, StripePriceChangeRequest request);
    /// <summary>All invoices of a subscription, newest first</summary>
    Task<IReadOnlyList<SubscriptionInvoice>> ListInvoices(string subscriptionId);
    /// <summary>Refunds the payment of an invoice, fully when <paramref name="amountCents"/> is null</summary>
    Task<RefundResponse> RefundInvoice(string invoiceId, long? amountCents);
    /// <summary>The invoice paid by a payment intent, null for one-time payments</summary>
    Task<StripeInvoiceInfo> FindInvoiceByPaymentIntent(string paymentIntentId);
}

/// <summary>
/// The parts of a Stripe subscription this service uses
/// </summary>
public record StripeSubscriptionSnapshot(
    string Id,
    string Status,
    bool CancelAtPeriodEnd,
    DateTime? CancelAt,
    DateTime? CurrentPeriodEnd,
    DateTime? EndedAt,
    string CustomerId,
    IReadOnlyDictionary<string, string> Metadata,
    string ItemId,
    string PriceId,
    string StripeProductId,
    long? UnitAmount,
    string Currency,
    string TaxBehavior,
    bool ManagedPayments,
    DateTime Created,
    bool HasPendingUpdate = false);

/// <summary>
/// A price swap on the single subscription item
/// </summary>
/// <param name="UnitAmount">New recurring amount in cents: gross incl. VAT for direct subscriptions, net for Managed Payments</param>
/// <param name="Currency">Lower case ISO currency</param>
/// <param name="IntervalWeeks">Billing interval in weeks (unchanged by plan switches)</param>
/// <param name="InvoiceNow">Upgrade: invoice the proration immediately; the new price only applies once that
/// invoice is paid (pending_if_incomplete). Downgrade: no proration, the new price starts with the next renewal.</param>
/// <param name="TaxBehavior">Kept from the current price ("exclusive" for Managed Payments, where the amount is net; null for direct, where it is the final gross)</param>
/// <param name="PlanSlug">The plan billed after the change; its Stripe product identifies the plan</param>
/// <param name="PlanName">Product name shown to the buyer</param>
public record StripePriceChangeRequest(long UnitAmount, string Currency, int IntervalWeeks, bool InvoiceNow,
    string TaxBehavior, string PlanSlug, string PlanName);

/// <summary>
/// Which plan a Stripe subscription bills. Plan switches move the item to a Stripe product with a deterministic id
/// per plan, so the billed plan changes atomically with the price (also for pending upgrades, which can not change
/// metadata). Subscriptions that were never switched still bill the checkout's inline product: their plan is the
/// checkout metadata.
/// </summary>
public static class StripePlans
{
    public const string ProductPrefix = "coflnet_plan_";

    public static string ProductId(string planSlug) => ProductPrefix + planSlug;

    public static string SlugOf(StripeSubscriptionSnapshot snapshot) =>
        snapshot.StripeProductId?.StartsWith(ProductPrefix, StringComparison.Ordinal) == true
            ? snapshot.StripeProductId[ProductPrefix.Length..]
            : snapshot.Metadata.GetValueOrDefault(SubscriptionService.StripeMetadata.ProductSlug);
}

/// <summary>
/// Result of a price swap; <paramref name="InvoiceId"/> is the proration invoice of an upgrade
/// </summary>
public record StripePriceChange(StripeSubscriptionSnapshot Subscription, string InvoiceId, string InvoiceStatus);

/// <summary>
/// The parts of a Stripe invoice this service uses
/// </summary>
public record StripeInvoiceInfo(
    string Id,
    string SubscriptionId,
    string BillingReason,
    string Status,
    long Total,
    long AmountPaid,
    string Currency,
    DateTime Created,
    string CustomerCountry,
    string CustomerPostalCode,
    string CustomerEmail,
    string CustomerName,
    IReadOnlyDictionary<string, string> SubscriptionMetadata,
    long Tax = 0)
{
    /// <summary>
    /// Maps Stripe billing reasons to the vocabulary the refund code shares with Lemon Squeezy
    /// ("initial", "renewal", "updated").
    /// </summary>
    public string NormalizedBillingReason => BillingReason switch
    {
        "subscription_create" => "initial",
        "subscription_cycle" => "renewal",
        "subscription_update" => "updated",
        _ => BillingReason
    };

    /// <summary>
    /// Reads a webhook invoice (Stripe.net 51: the subscription is under parent.subscription_details)
    /// </summary>
    public static StripeInvoiceInfo From(Stripe.Invoice invoice)
    {
        var details = invoice.Parent?.SubscriptionDetails;
        return new StripeInvoiceInfo(invoice.Id, details?.SubscriptionId, invoice.BillingReason, invoice.Status,
            invoice.Total, invoice.AmountPaid, invoice.Currency, invoice.Created,
            invoice.CustomerAddress?.Country?.ToUpperInvariant(), invoice.CustomerAddress?.PostalCode,
            invoice.CustomerEmail, invoice.CustomerName,
            new Dictionary<string, string>(details?.Metadata ?? new Dictionary<string, string>()),
            invoice.TotalTaxes?.Sum(t => t.Amount) ?? 0);
    }
}
