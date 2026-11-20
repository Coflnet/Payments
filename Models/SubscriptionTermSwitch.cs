using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// A buyer's switch of a running direct subscription to a longer term of the same tier (4 → 13 or 52 weeks, 13 → 52), charged at
/// once. The paid days left of the current term are kept: the new term is granted on top of the current access end. One row per
/// attempt; the payment that paid it (<see cref="InvoiceId"/>) is granted exactly once as a full term of <see cref="Product"/>.
/// </summary>
public class SubscriptionTermSwitch
{
    public long Id { get; set; }
    /// <summary>The switched subscription (Stripe: it continues with the new term; PayPal: the one the new subscription replaces)</summary>
    [MaxLength(64)]
    public string SubscriptionId { get; set; }
    [MaxLength(16)]
    public string Provider { get; set; }
    /// <summary>The buyer (external user id)</summary>
    [MaxLength(64)]
    public string UserId { get; set; }
    /// <summary>PayPal: the new PayPal subscription of the new term (pending the payer's approval until it is linked)</summary>
    [MaxLength(64)]
    public string NewSubscriptionId { get; set; }
    /// <summary>The plan before the switch</summary>
    public Product PreviousProduct { get; set; }
    /// <summary>The 13 or 52 week plan switched to</summary>
    public Product Product { get; set; }
    /// <summary>The 4-week plan the new term continues as after it (§ 309 Nr. 9 b BGB)</summary>
    public Product RenewalProduct { get; set; }
    /// <summary>Length of the new term in weeks</summary>
    public int Weeks { get; set; }
    /// <summary>What the new term costs without a promotion (direct: gross incl. VAT, Managed Payments: net)</summary>
    public long RegularCents { get; set; }
    /// <summary>What is charged now (the regular price, or lower with the promotion); the paying invoice must charge exactly this</summary>
    public long ChargedCents { get; set; }
    /// <summary>What each 4-week cycle after the new term charges</summary>
    public long RenewalCents { get; set; }
    [MaxLength(3)]
    public string Currency { get; set; }
    /// <summary>The automatic promotion applied to the first charge, null without one</summary>
    [MaxLength(50)]
    public string PromotionCode { get; set; }
    /// <summary>The access end when the switch was requested (at least then); the new term is added on top of it</summary>
    public DateTime AccessEndsBefore { get; set; }
    /// <summary>The provider payment that paid the new term (Stripe invoice, PayPal sale); set once known</summary>
    [MaxLength(64)]
    public string InvoiceId { get; set; }
    /// <summary>Where the buyer completes a pending switch: Stripe's hosted invoice (payment confirmation), PayPal's approval link</summary>
    [MaxLength(1024)]
    public string PaymentUrl { get; set; }
    /// <summary>PayPal: the payment request the new PayPal subscription is activated from</summary>
    public int? PaymentRequestId { get; set; }
    /// <summary>One of <see cref="TermSwitchStatus"/>; changed with conditional updates only</summary>
    [MaxLength(16)]
    public string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>A pending switch blocks another one until then (Stripe drops an unpaid pending update after 23 hours)</summary>
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Values of <see cref="SubscriptionTermSwitch.Status"/></summary>
public static class TermSwitchStatus
{
    /// <summary>Waiting for the payment (Stripe payment confirmation, PayPal approval)</summary>
    public const string Pending = "pending";
    /// <summary>Paid and granted (Stripe) or approved and linked (PayPal)</summary>
    public const string Completed = "completed";
    /// <summary>Not applied (provider refused, replaced by another switch, refused on approval, or it could not be granted)</summary>
    public const string Failed = "failed";
    /// <summary>
    /// Not paid or approved in time (its promotion use was given back). Still completed when its payment or approval arrives late:
    /// a Stripe invoice that pays it, a PayPal approval (unlike a replaced, failed switch)
    /// </summary>
    public const string Expired = "expired";
}

/// <summary>Switch to a longer term of the same tier, charged now</summary>
public class SubscriptionTermSwitchRequest
{
    /// <summary>The new term in weeks (13 or 52), one of the offered <see cref="SubscriptionTermOptions.Terms"/></summary>
    public int Weeks { get; set; }
    /// <summary>The early performance declaration of the new term (required)</summary>
    [Required]
    public SubscriptionDeclaration Declaration { get; set; }
    /// <summary>
    /// Required: the price shown to the buyer (<see cref="SubscriptionTermOption.ChargedNow"/>); a switch that would charge another
    /// amount (e.g. the promotion ended meanwhile) is refused with <c>term_switch_price_changed</c>, a missing one with
    /// <c>expected_charged_now_required</c>
    /// </summary>
    public decimal? ExpectedChargedNow { get; set; }
    /// <summary>PayPal: where PayPal sends the buyer after approving the new subscription</summary>
    public string ReturnUrl { get; set; }
    /// <summary>PayPal: where PayPal sends the buyer when the approval is cancelled</summary>
    public string CancelUrl { get; set; }
}

/// <summary>The longer terms a subscription can switch to now</summary>
public class SubscriptionTermOptions
{
    public string SubscriptionId { get; set; }
    /// <summary>Length of the current term in weeks</summary>
    public int? CurrentWeeks { get; set; }
    /// <summary>When the access paid so far ends</summary>
    public DateTime? CurrentAccessEndsAt { get; set; }
    /// <summary>Why no term is offered (e.g. <c>term_switch_unavailable</c> while switched off), null when there are terms</summary>
    public string UnavailableReason { get; set; }
    /// <summary>A switch waiting for the buyer (payment confirmation or PayPal approval), null without one</summary>
    public SubscriptionTermSwitchResult Pending { get; set; }
    public List<SubscriptionTermOption> Terms { get; set; } = [];
}

/// <summary>
/// One longer term: what it costs now, how the paid days left are kept and how it continues afterwards. Prices are in
/// <see cref="Currency"/>; <see cref="TaxTreatment"/> says how tax relates to them ("included", "net": the tax is added, "none").
/// </summary>
public class SubscriptionTermOption
{
    public int Weeks { get; set; }
    public string ProductSlug { get; set; }
    public string Title { get; set; }
    /// <summary>The term's price without a promotion (with the subscription's creator code and lasting discount code)</summary>
    public decimal RegularPrice { get; set; }
    /// <summary>What is charged now: the regular price, or the promotion price when lower</summary>
    public decimal ChargedNow { get; set; }
    public string Currency { get; set; }
    /// <summary>How tax relates to the prices: "included", "net" (Stripe adds it, Managed Payments) or "none" (e.g. the US)</summary>
    public string TaxTreatment { get; set; }
    /// <summary>The promotion lowering <see cref="ChargedNow"/>, null without one (it applies to this first charge only)</summary>
    public SubscriptionTermPromotion Promotion { get; set; }
    /// <summary>When the access paid so far ends; these days are kept, nothing is refunded</summary>
    public DateTime CurrentAccessEndsAt { get; set; }
    /// <summary>When the access ends after the switch: <see cref="CurrentAccessEndsAt"/> plus the new term</summary>
    public DateTime NewAccessEndsAt { get; set; }
    /// <summary>Whole days of the current term that are carried over on top of the new term</summary>
    public int CarriedOverDays { get; set; }
    /// <summary>When the first 4-week cycle after the new term is charged (the billing date restarts with the switch)</summary>
    public DateTime FirstRenewalAt { get; set; }
    /// <summary>The 4-week plan the subscription continues as after the new term</summary>
    public string RenewsAsProductSlug { get; set; }
    /// <summary>What each 4-week cycle after the new term charges, with <see cref="TaxTreatment"/></summary>
    public decimal RenewalPrice { get; set; }
    public int RenewalWeeks { get; set; }
}

/// <summary>An automatic promotion applied to the first charge of a switch</summary>
public class SubscriptionTermPromotion
{
    public string Code { get; set; }
    public string Name { get; set; }
    /// <summary>"percent" or "fixed" (cents)</summary>
    public string AmountType { get; set; }
    /// <summary>The amount for this term's length (percent, or cents for "fixed")</summary>
    public decimal Amount { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>The outcome of a switch request</summary>
public class SubscriptionTermSwitchResult
{
    /// <summary>True once the new term is paid and granted (Stripe); false while the buyer still has to pay or approve</summary>
    public bool Switched { get; set; }
    /// <summary>"completed", "payment_required" (Stripe: confirm the payment at <see cref="ApprovalUrl"/>),
    /// "approval_required" (PayPal: approve the new subscription at <see cref="ApprovalUrl"/>) or "processing"</summary>
    public string Status { get; set; }
    /// <summary>Where to send the buyer to finish a pending switch, null otherwise</summary>
    public string ApprovalUrl { get; set; }
    public int Weeks { get; set; }
    public string ProductSlug { get; set; }
    public decimal ChargedNow { get; set; }
    public string Currency { get; set; }
    public string TaxTreatment { get; set; }
    public string PromotionCode { get; set; }
    /// <summary>When the access ends once the switch is paid (the current access end plus the new term)</summary>
    public DateTime AccessUntil { get; set; }
    /// <summary>When the first 4-week cycle after the new term is charged</summary>
    public DateTime FirstRenewalAt { get; set; }
    public string RenewsAsProductSlug { get; set; }
    public decimal RenewalPrice { get; set; }
    public int RenewalWeeks { get; set; }
}
