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
    /// <summary>
    /// Provider credential used to verify and manage the subscription (Google Play purchase token).
    /// </summary>
    [Column(TypeName = "text")]
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    public string ProviderToken { get; set; }
    public string ExternalCustomerId { get; set; }
    public DateTime RenewsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string PaymentAmount { get; set; }
    public string Status { get; set; }
    /// <summary>
    /// When the trial ends if subscription is in trial period (on_trial status). Direct providers: the end of the free trial this
    /// service granted (set once when the provider confirmed the trial; stays as history after it)
    /// </summary>
    public DateTime? TrialEndsAt { get; set; }
    /// <summary>
    /// Lemon Squeezy: when the user started their trial for this product. Direct providers: when the trial's confirmation was
    /// published (null until then, so a failed publish is retried)
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
    /// The term end (<see cref="RenewsAt"/>) the renewal reminder was published for; one reminder per term end
    /// </summary>
    public DateTime? RenewalReminderSentFor { get; set; }
    /// <summary>
    /// The term end at which the buyer's opt-in to one more term of the same length (instead of continuing every 4 weeks) starts.
    /// Booked for the current term while it equals <see cref="RenewsAt"/>; after that term was paid it is history.
    /// </summary>
    public DateTime? ExtensionStartsAt { get; set; }
    /// <summary>
    /// What the booked extension term charges, like <see cref="PriceCents"/> (direct: gross with the VAT of the checkout country,
    /// Managed Payments: net). Creator code and "forever" discount code applied, "once" codes not.
    /// </summary>
    public long? ExtensionPriceCents { get; set; }
    /// <summary>
    /// PayPal: the new PayPal subscription that continues this one for another term (pending the buyer's approval until it is
    /// linked, see <see cref="PreviousSubscriptionId"/>)
    /// </summary>
    [MaxLength(64)]
    public string ExtensionSubscriptionId { get; set; }
    /// <summary>
    /// PayPal: the subscription this one continues for another term (set once it was approved and the previous one cancelled)
    /// </summary>
    [MaxLength(64)]
    public string PreviousSubscriptionId { get; set; }
    /// <summary>
    /// PayPal: when this subscription, approved as the extension of another one, was refused (the other one was cancelled, ended
    /// or converted before the approval). Its payments are never granted; they are marked for a manual refund.
    /// </summary>
    public DateTime? ContinuationRefusedAt { get; set; }
    /// <summary>
    /// End of the current paid term (response only)
    /// </summary>
    [NotMapped]
    public DateTime CurrentTermEndsAt { get; set; }
    /// <summary>
    /// Whether the buyer can opt in to one more term of the same length before this 13 or 52 week term ends (response only)
    /// </summary>
    [NotMapped]
    public bool CanExtendLongTerm { get; set; }
    /// <summary>
    /// What one more term of the same length costs in <see cref="Currency"/> (the booked price once booked; 0 when it can not
    /// be extended), with <see cref="ExtensionTaxTreatment"/> (response only)
    /// </summary>
    [NotMapped]
    public decimal ExtensionPrice { get; set; }
    /// <summary>
    /// How tax relates to <see cref="ExtensionPrice"/> and <see cref="RenewalPrice"/>: "included", "net" or "none" (response only)
    /// </summary>
    [NotMapped]
    public string ExtensionTaxTreatment { get; set; }
    /// <summary>
    /// What each 4-week cycle charges after the current term in <see cref="Currency"/> (response only)
    /// </summary>
    [NotMapped]
    public decimal RenewalPrice { get; set; }
    /// <summary>
    /// The end of the booked extension term, null when none is booked (response only)
    /// </summary>
    [NotMapped]
    public DateTime? ExtendedUntil { get; set; }
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
    /// <summary>
    /// Where the buyer can manage the subscription with its provider (response only)
    /// </summary>
    [NotMapped]
    public string ManageUrl { get; set; }
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