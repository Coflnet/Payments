namespace Coflnet.Payments.Models;

public class UserSubscription
{
    public int Id { get; set; }
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