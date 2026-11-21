using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Provider specific operations on an existing subscription. Implementations update the
/// passed <see cref="UserSubscription"/> in memory; <see cref="SubscriptionService"/> persists it.
/// </summary>
public interface ISubscriptionProvider
{
    /// <summary>
    /// The value stored in <see cref="UserSubscription.ProviderSlug"/>
    /// </summary>
    string Slug { get; }
    /// <summary>
    /// Whether a cancelled subscription can be reactivated before it ends
    /// </summary>
    bool CanResume(UserSubscription subscription);
    /// <summary>
    /// Whether the plan can be switched with this provider
    /// </summary>
    bool CanChangePlan(UserSubscription subscription);
    /// <summary>
    /// Whether this provider can bill <paramref name="plan"/> after a plan switch
    /// </summary>
    bool OffersPlan(TopUpProduct plan);
    /// <summary>
    /// Stops renewals; access lasts until the paid period ends
    /// </summary>
    Task CancelAtPeriodEnd(UserSubscription subscription);
    /// <summary>
    /// Undoes <see cref="CancelAtPeriodEnd"/> while the period is still running
    /// </summary>
    Task Resume(UserSubscription subscription);
    /// <summary>
    /// Switches to another plan of the same interval and slot count
    /// </summary>
    Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target);
    /// <summary>
    /// All invoices (payments) of the subscription
    /// </summary>
    Task<IReadOnlyList<SubscriptionInvoice>> GetInvoices(UserSubscription subscription);
    /// <summary>
    /// Refunds (part of) one invoice with the provider
    /// </summary>
    Task<RefundResponse> RefundInvoice(UserSubscription subscription, SubscriptionInvoice invoice, int? amountCents);
}

/// <summary>
/// Provider slugs and the per-provider grant reference scheme
/// </summary>
public static class SubscriptionProviders
{
    /// <summary>Lemon Squeezy (merchant of record, being phased out for new subscribers)</summary>
    public const string LemonSqueezy = "lemonsqueezy";
    /// <summary>Stripe Billing, direct or Managed Payments</summary>
    public const string Stripe = "stripe";
    /// <summary>PayPal Subscriptions v1</summary>
    public const string PayPal = "paypal";
    /// <summary>Google Play Billing</summary>
    public const string GooglePlay = "googleplay";

    /// <summary>
    /// Rows created before providers were recorded are Lemon Squeezy subscriptions.
    /// </summary>
    public static string Normalize(string slug) => string.IsNullOrWhiteSpace(slug) ? LemonSqueezy : slug;

    /// <summary>
    /// The FiniteTransaction reference of the paid period granted for one provider invoice/sale.
    /// The coin credit uses the same reference with "-topup" appended.
    /// </summary>
    public static string GrantReference(string providerSlug, string invoiceId) => Normalize(providerSlug) switch
    {
        Stripe => "stripe-invoice-" + invoiceId,
        PayPal => "paypal-sale-" + invoiceId,
        GooglePlay => "googleplay-order-" + invoiceId,
        _ => "ls-invoice-" + invoiceId
    };
}
