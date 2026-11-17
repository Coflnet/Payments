using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// The PayPal billing plan created for a subscription product, billing period and currency.
/// Unique on (<see cref="ProductSlug"/>, <see cref="BillingWeeks"/>, <see cref="Currency"/>).
/// </summary>
public class PayPalPlan
{
    public int Id { get; set; }
    /// <summary>Slug of the subscription product</summary>
    [Required, MaxLength(100)]
    public string ProductSlug { get; set; }
    /// <summary>Billing period: 4, 13 or 52 weeks</summary>
    public int BillingWeeks { get; set; }
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
