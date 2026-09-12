using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

// A proration refund must restore the tier it paid for, without undoing a later renewal.
public class SubscriptionPlanChange
{
    public long Id { get; set; }
    [MaxLength(64)]
    public string SubscriptionId { get; set; }
    public Product PreviousProduct { get; set; }
    public Product Product { get; set; }
    public DateTime ChangedAt { get; set; }
    public DateTime PeriodEnd { get; set; }
    [MaxLength(64)]
    public string InvoiceId { get; set; }
    public bool Refunded { get; set; }
}

public class RefundedSubscriptionInvoice
{
    [Key, MaxLength(64)]
    public string InvoiceId { get; set; }
    [MaxLength(64)]
    public string SubscriptionId { get; set; }
    public string BillingReason { get; set; }
}
