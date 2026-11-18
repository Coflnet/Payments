using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Coflnet.Payments.Models;

public class UserSubscription
{
    public int Id { get; set; }
    public long? ProviderVariantId { get; set; }
    public DateTime? ChangeLockUntil { get; set; }
    public User User { get; set; }
    public Product Product { get; set; }
    public string ProviderSlug { get; set; }
    public string ExternalId { get; set; }
    public string ExternalCustomerId { get; set; }
    public DateTime RenewsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string PaymentAmount { get; set; }
    public string Status { get; set; }
    /// <summary>
    /// When the trial ends if subscription is in trial period (on_trial status)
    /// </summary>
    public DateTime? TrialEndsAt { get; set; }
    /// <summary>
    /// When the user started their trial for this product (used to prevent multiple trials)
    /// </summary>
    public DateTime? TrialUsedAt { get; set; }
    /// <summary>
    /// The provider's id for the price/plan currently billed (Stripe price id, PayPal plan id).
    /// </summary>
    [MaxLength(64)]
    public string ProviderPlanId { get; set; }
    /// <summary>
    /// True when the provider is merchant of record (Stripe Managed Payments) and remits the tax.
    /// </summary>
    public bool ManagedPayments { get; set; }
    /// <summary>
    /// The recurring gross price agreed at checkout in minor units of <see cref="Currency"/>.
    /// Direct providers verify paid amounts against it.
    /// </summary>
    public long? PriceCents { get; set; }
    /// <summary>
    /// ISO currency of <see cref="PriceCents"/> (upper case)
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; }
    /// <summary>
    /// Local discount code applied to the recurring price at checkout
    /// </summary>
    [MaxLength(50)]
    public string DiscountCode { get; set; }
    /// <summary>
    /// Creator code used at checkout
    /// </summary>
    [MaxLength(50)]
    public string CreatorCode { get; set; }
    /// <summary>
    /// The buyer country the checkout was priced for (the VAT country of direct sales), fixed for the subscription's lifetime.
    /// Null for Lemon Squeezy subscriptions. The US direct subscription cap counts by it.
    /// </summary>
    [MaxLength(2)]
    public string CheckoutCountry { get; set; }
    /// <summary>
    /// The buyer's state or province at checkout (ISO 3166-2 subdivision code without the country prefix, e.g. "CA"), from the edge
    /// at checkout time, fixed for the subscription's lifetime. Null when unknown (and for subscriptions made before it existed).
    /// The US direct subscription cap counts by it (indexed together with the country and status).
    /// </summary>
    [MaxLength(3)]
    public string CheckoutRegion { get; set; }
    /// <summary>
    /// The 4-week plan of the same tier a 13 or 52 week subscription continues as after its first term (§ 309 Nr. 9 b BGB:
    /// automatic renewals only for an indefinite term cancellable with at most one month's notice). Null for plans that
    /// bill every 4 weeks anyway, Lemon Squeezy and PayPal subscriptions on a plan without a 4-week regular cycle.
    /// The subscription is converted once its first renewal is paid: then <see cref="Product"/> is this plan.
    /// </summary>
    public Product RenewalProduct { get; set; }
    /// <summary>
    /// What every 4-week renewal of <see cref="RenewalProduct"/> charges, like <see cref="PriceCents"/>: direct sales gross
    /// with the VAT of the checkout country, Managed Payments net. Creator code and "forever" discount code applied, "once" codes not.
    /// </summary>
    public long? RenewalPriceCents { get; set; }
    /// <summary>
    /// Stripe: the subscription schedule that moves the subscription to <see cref="RenewalProduct"/> at the end of its first term
    /// </summary>
    [MaxLength(64)]
    public string RenewalScheduleId { get; set; }
    /// <summary>
    /// Whether a cancelled subscription can be resumed with its provider (response only)
    /// </summary>
    [NotMapped]
    public bool CanResume { get; set; }
    /// <summary>
    /// Whether the subscription's provider supports plan switches (response only)
    /// </summary>
    [NotMapped]
    public bool CanChangePlan { get; set; }
}

/// <summary>
/// Tracks trial usage per user/product combination to prevent abuse
/// </summary>
public class TrialUsage
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// Foreign key to the user
    /// </summary>
    public int UserId { get; set; }
    /// <summary>
    /// The user who used the trial
    /// </summary>
    public User User { get; set; }
    /// <summary>
    /// Foreign key to the product
    /// </summary>
    public int ProductId { get; set; }
    /// <summary>
    /// The product for which trial was used
    /// </summary>
    public Product Product { get; set; }
    /// <summary>
    /// When the trial was started
    /// </summary>
    public DateTime TrialStartedAt { get; set; }
    /// <summary>
    /// The external subscription ID from the payment provider
    /// </summary>
    public string ExternalSubscriptionId { get; set; }
}