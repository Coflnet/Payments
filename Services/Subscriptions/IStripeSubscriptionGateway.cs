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
    /// <summary>Creates a customer with the supplied email and metadata and returns its id</summary>
    Task<string> CreateCustomer(string email, Dictionary<string, string> metadata);
    /// <summary>Creates a hosted checkout session</summary>
    Task<Session> CreateCheckoutSession(SessionCreateOptions options);
    /// <summary>Loads an existing checkout session (to reuse its URL)</summary>
    Task<Session> GetCheckoutSession(string sessionId);
    /// <summary>The current status of a dispute (won, lost, needs_response, ...)</summary>
    Task<string> GetDisputeStatus(string disputeId);
    /// <summary>Loads the current subscription state</summary>
    Task<StripeSubscriptionSnapshot> GetSubscription(string subscriptionId);
    /// <summary>Sets or clears cancel_at_period_end</summary>
    Task<StripeSubscriptionSnapshot> SetCancelAtPeriodEnd(string subscriptionId, bool cancel);
    /// <summary>Ends the subscription immediately, without a refund</summary>
    Task<StripeSubscriptionSnapshot> CancelNow(string subscriptionId);
    /// <summary>Swaps the price (and plan product) of the single subscription item</summary>
    Task<StripePriceChange> ChangePrice(StripeSubscriptionSnapshot current, StripePriceChangeRequest request);
    /// <summary>
    /// Moves the subscription to a longer term now: the item bills the new term's price, the billing cycle restarts now and the
    /// term is invoiced at once without proration credit. The change only applies once that invoice is paid
    /// (pending_if_incomplete), so a payment that needs the buyer's confirmation (SCA) leaves a pending update. The item names a
    /// Price created for it (<see cref="StripePlanPrices"/>): pending updates do not accept price_data.
    /// </summary>
    Task<StripeTermSwitch> SwitchTerm(StripeSubscriptionSnapshot current, StripeTermSwitchRequest request);
    /// <summary>All invoices of a subscription, newest first</summary>
    Task<IReadOnlyList<SubscriptionInvoice>> ListInvoices(string subscriptionId);
    /// <summary>Refunds the payment of an invoice, fully when <paramref name="amountCents"/> is null</summary>
    Task<RefundResponse> RefundInvoice(string invoiceId, long? amountCents);
    /// <summary>The invoice paid by a payment intent, null for one-time payments</summary>
    Task<StripeInvoiceInfo> FindInvoiceByPaymentIntent(string paymentIntentId);
    /// <summary>
    /// Returns the id of a coupon with exactly the spec's terms: the deterministic id when it is missing (created) or matches,
    /// otherwise a versioned id (<see cref="StripeFirstPaymentCoupon.VersionedId"/>) with an error logged
    /// </summary>
    Task<string> EnsureFirstPaymentCoupon(StripeCouponSpec spec);
    /// <summary>The inclusive manual "VAT" tax rate of a country (created once, then reused), for the receipts of direct sales</summary>
    Task<string> EnsureVatTaxRate(string country, int basisPoints);
    /// <summary>Issuing country of the card that paid an invoice (ISO code), null for other payment methods</summary>
    Task<string> GetCardCountry(string invoiceId);
    /// <summary>
    /// Makes the subscription continue as <paramref name="request"/> after its current period: attaches a subscription
    /// schedule (from_subscription, or completes the one already attached) whose current phase ends with the period and
    /// whose next phase bills the renewal price every 4 weeks; after that phase's first period the schedule releases the
    /// subscription, which keeps billing every 4 weeks. Returns the schedule id.
    /// </summary>
    Task<string> ScheduleRenewal(StripeSubscriptionSnapshot current, StripeRenewalRequest request);
    /// <summary>Detaches a subscription schedule; the subscription keeps its current price and period</summary>
    Task<StripeSubscriptionSnapshot> ReleaseSchedule(string scheduleId, string subscriptionId);
    /// <summary>Whether a schedule is ours (<see cref="StripeRenewalSchedule.MarkerKey"/>) or has changes planned that we did not make</summary>
    Task<StripeScheduleInfo> GetSchedule(string scheduleId);
}

/// <summary>
/// A subscription schedule; <paramref name="HasForeignPhases"/>: someone else planned changes with it; <paramref name="IsRenewalSchedule"/>:
/// it carries our renewal marker; <paramref name="Marker"/>: that marker (<see cref="StripeRenewalSchedule.Marker"/>), null without one
/// </summary>
public record StripeScheduleInfo(string Id, bool HasForeignPhases, bool IsRenewalSchedule = false, string Marker = null);

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
    bool HasPendingUpdate = false,
    string ScheduleId = null,
    string Interval = null,
    long? IntervalCount = null,
    DateTime? TrialEnd = null)
{
    /// <summary>The billed price's period in weeks (week intervals only), null for other intervals or when unknown</summary>
    public int? IntervalWeeks => Interval == "week" && IntervalCount is long count ? (int)count : null;
}

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
/// A switch to a longer term, invoiced now
/// </summary>
/// <param name="UnitAmount">Cents of the new term as charged now (a promotion is part of it: the term's price is billed once, its
/// renewal schedule then moves the subscription to the 4-week price); gross incl. VAT for direct subscriptions, net for Managed Payments</param>
/// <param name="Currency">Lower case ISO currency</param>
/// <param name="Weeks">The new term (13 or 52)</param>
/// <param name="TaxBehavior">Kept from the current price ("exclusive" for Managed Payments, null for direct)</param>
/// <param name="PlanSlug">The plan of the new term; its Stripe product identifies it</param>
/// <param name="PlanName">Product name shown to the buyer</param>
public record StripeTermSwitchRequest(long UnitAmount, string Currency, int Weeks, string TaxBehavior, string PlanSlug, string PlanName);

/// <summary>
/// Result of a term switch: the subscription after the update (a pending update until the invoice is paid), the invoice of the new
/// term and its hosted page, where the buyer confirms a payment that needs it
/// </summary>
public record StripeTermSwitch(StripeSubscriptionSnapshot Subscription, StripeInvoiceInfo Invoice, string PaymentUrl);

/// <summary>
/// The 4-week price a 13 or 52 week subscription continues with after its current period
/// </summary>
/// <param name="UnitAmount">Cents every 4 weeks: gross incl. VAT for direct subscriptions, net for Managed Payments</param>
/// <param name="Currency">Lower case ISO currency</param>
/// <param name="TaxBehavior">Kept from the current price ("exclusive" for Managed Payments, null for direct)</param>
/// <param name="PlanSlug">The 4-week plan; its Stripe product (<see cref="StripePlans.ProductId"/>) identifies it once billed</param>
/// <param name="PlanName">Product name shown to the buyer</param>
/// <param name="TermEnd">End of the paid first term (the subscription's current period end), where the renewal phase starts</param>
/// <param name="TaxRateId">Direct sales with VAT: the inclusive VAT rate shown on the receipts, as on the checkout</param>
/// <param name="Extension">The term of the same length the buyer opted in to before the 4-week renewal, null without one</param>
public record StripeRenewalRequest(long UnitAmount, string Currency, string TaxBehavior, string PlanSlug, string PlanName,
    DateTime TermEnd, string TaxRateId = null, StripeExtensionTerm Extension = null);

/// <summary>
/// One more term of the long plan, billed from <see cref="StripeRenewalRequest.TermEnd"/> before the 4-week renewal
/// </summary>
/// <param name="UnitAmount">Cents of the term: gross incl. VAT for direct subscriptions, net for Managed Payments</param>
/// <param name="PlanSlug">The 13 or 52 week plan; its Stripe product identifies it once billed</param>
/// <param name="PlanName">Product name shown to the buyer</param>
/// <param name="Weeks">Length of the term (13 or 52)</param>
public record StripeExtensionTerm(long UnitAmount, string PlanSlug, string PlanName, int Weeks);

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
    long Tax = 0,
    string CustomerState = null)
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
            invoice.TotalTaxes?.Sum(t => t.Amount) ?? 0, invoice.CustomerAddress?.State?.ToUpperInvariant());
    }
}
