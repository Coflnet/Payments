using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// The buyer opts in to one more term of the same length (52 → another 52 weeks, 13 → another 13) instead of continuing
/// every 4 weeks after the current 13 or 52 week term
/// </summary>
public class SubscriptionExtensionRequest
{
    /// <summary>The early performance declaration given for the extension term (required)</summary>
    [Required]
    public SubscriptionDeclaration Declaration { get; set; }
    /// <summary>PayPal: where PayPal sends the buyer after approving the extension</summary>
    public string ReturnUrl { get; set; }
    /// <summary>PayPal: where PayPal sends the buyer when the approval is cancelled</summary>
    public string CancelUrl { get; set; }
}

/// <summary>
/// The booked (Stripe) or approvable (PayPal) extension
/// </summary>
public class SubscriptionExtensionResult
{
    /// <summary>True when the extension is booked (Stripe); false while PayPal waits for the buyer's approval</summary>
    public bool Extended { get; set; }
    /// <summary>PayPal: the link the buyer approves the extension at; null for Stripe</summary>
    public string ApprovalUrl { get; set; }
    /// <summary>End of the current term, where the extension term starts</summary>
    public DateTime TermEndsAt { get; set; }
    /// <summary>End of the extension term</summary>
    public DateTime ExtendedUntil { get; set; }
}
