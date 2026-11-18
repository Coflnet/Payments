using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// The PayPal billing plan created for a subscription product, billing period, renewal period and currency.
/// Unique on (<see cref="ProductSlug"/>, <see cref="BillingWeeks"/>, <see cref="RenewalWeeks"/>, <see cref="Currency"/>).
/// </summary>
public class PayPalPlan
{
    public int Id { get; set; }
    /// <summary>Slug of the subscription product</summary>
    [Required, MaxLength(100)]
    public string ProductSlug { get; set; }
    /// <summary>Billing period of the first cycle (TRIAL): 4, 13 or 52 weeks</summary>
    public int BillingWeeks { get; set; }
    /// <summary>
    /// Period of the following cycles (REGULAR). Plans created since the 4-week renewal continue every 4 weeks; the 13 and
    /// 52 week plans created before repeat their <see cref="BillingWeeks"/> (the migration set them to that).
    /// </summary>
    public int RenewalWeeks { get; set; }
    /// <summary>Upper case ISO currency</summary>
    [Required, MaxLength(3)]
    public string Currency { get; set; }
    /// <summary>PayPal plan id (P-...)</summary>
    [Required, MaxLength(64)]
    public string PlanId { get; set; }
    /// <summary>PayPal catalog product id (PROD-...)</summary>
    [MaxLength(64)]
    public string PayPalProductId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
