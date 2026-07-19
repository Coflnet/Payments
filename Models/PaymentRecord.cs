using System;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// Authoritative record of exactly what was paid by a user for tax compliance.
/// One row per confirmed payment — tracks the real money flow including
/// discounts, taxes, fees, and creator code attribution.
/// </summary>
public class PaymentRecord
{
    /// <summary>
    /// Primary key
    /// </summary>
    public long Id { get; set; }

    // ── Who ──────────────────────────────────────────────

    /// <summary>
    /// FK to the internal User table
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Navigation property
    /// </summary>
    public User User { get; set; }

    /// <summary>
    /// External user ID (denormalised for fast export queries)
    /// </summary>
    [MaxLength(32)]
    public string ExternalUserId { get; set; }

    // ── Where (tax jurisdiction) ─────────────────────────

    /// <summary>
    /// ISO 3166-1 alpha-2 country code at time of purchase
    /// </summary>
    [MaxLength(2)]
    public string Country { get; set; }

    /// <summary>
    /// Postal/ZIP code at time of purchase (needed for US state-level tax)
    /// </summary>
    [MaxLength(10)]
    public string ZipCode { get; set; }

    /// <summary>
    /// State/province at time of purchase (e.g. US state, CA province)
    /// </summary>
    [MaxLength(10)]
    public string State { get; set; }

    /// <summary>
    /// City at time of purchase
    /// </summary>
    [MaxLength(80)]
    public string City { get; set; }

    // ── What was paid ────────────────────────────────────

    /// <summary>
    /// Gross amount charged to the customer (before any internal discounts,
    /// but this IS what the payment processor collected)
    /// </summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// Subtotal before tax (provided by processor where available)
    /// </summary>
    public decimal Subtotal { get; set; }

    /// <summary>
    /// Discount applied by the payment processor (e.g. LemonSqueezy coupon)
    /// </summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>
    /// Tax amount already collected by the payment processor
    /// (LemonSqueezy and Google Play handle tax; Stripe/PayPal/CoinGate typically don't)
    /// </summary>
    public decimal TaxAmount { get; set; }

    /// <summary>
    /// Human-readable tax label (e.g. "VAT", "Sales Tax", "GST")
    /// </summary>
    [MaxLength(50)]
    public string TaxName { get; set; }

    /// <summary>
    /// Tax rate applied by the processor (percentage, e.g. 19.0 for 19%)
    /// </summary>
    public double TaxRate { get; set; }

    /// <summary>
    /// Whether the processor already remitted the tax on our behalf
    /// (true for LemonSqueezy MoR, Google Play; false for Stripe/PayPal/CoinGate)
    /// </summary>
    public bool TaxRemittedByProcessor { get; set; }

    /// <summary>
    /// Net amount we actually receive after processor fees and tax
    /// (GrossAmount − TaxAmount − ProcessorFee)
    /// </summary>
    public decimal NetAmount { get; set; }

    // ── Fees ─────────────────────────────────────────────

    /// <summary>
    /// Fee charged by the payment processor (when known)
    /// </summary>
    public decimal ProcessorFee { get; set; }

    // ── Creator code attribution ─────────────────────────

    /// <summary>
    /// Creator code used (if any)
    /// </summary>
    [MaxLength(50)]
    public string CreatorCode { get; set; }

    /// <summary>
    /// Discount from creator code (our own discount, separate from processor discount)
    /// </summary>
    public decimal CreatorCodeDiscount { get; set; }

    // ── Currency ─────────────────────────────────────────

    /// <summary>
    /// ISO 4217 currency code of the amounts above (e.g. "USD", "EUR")
    /// </summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; }

    /// <summary>
    /// Exchange rate to EUR at time of purchase (for normalised reporting)
    /// </summary>
    public decimal? ExchangeRateToEur { get; set; }

    // ── Payment method / provider ────────────────────────

    /// <summary>
    /// Provider slug: "stripe", "lemonsqueezy", "paypal", "coingate", "googlepay"
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Provider { get; set; }

    /// <summary>
    /// Payment method within the provider (e.g. "card", "paypal", "crypto", "BTC")
    /// </summary>
    [MaxLength(32)]
    public string PaymentMethod { get; set; }

    // ── External references ──────────────────────────────

    /// <summary>
    /// Primary external order/transaction ID from the payment processor
    /// </summary>
    [MaxLength(128)]
    public string ExternalOrderId { get; set; }

    /// <summary>
    /// Secondary external reference (e.g. Stripe PaymentIntent, PayPal capture ID)
    /// </summary>
    [MaxLength(128)]
    public string ExternalTransactionId { get; set; }

    // ── Internal references ──────────────────────────────

    /// <summary>
    /// Internal product slug (e.g. "premium_month")
    /// </summary>
    [MaxLength(80)]
    public string ProductSlug { get; set; }

    /// <summary>
    /// FK to TopUpProduct (if applicable)
    /// </summary>
    public int? ProductId { get; set; }

    /// <summary>
    /// Internal coins / virtual-currency amount credited
    /// </summary>
    public long CoinAmount { get; set; }

    // ── Timestamps ───────────────────────────────────────

    /// <summary>
    /// When the payment was confirmed / captured
    /// </summary>
    public DateTime PaidAt { get; set; }

    /// <summary>
    /// When this record was created in our system (might differ from PaidAt
    /// if webhook arrives late)
    /// </summary>
    public DateTime RecordedAt { get; set; }

    // ── Status ───────────────────────────────────────────

    /// <summary>
    /// Current status of this payment record
    /// </summary>
    public PaymentRecordStatus Status { get; set; }

    /// <summary>
    /// If refunded, when
    /// </summary>
    public DateTime? RefundedAt { get; set; }

    /// <summary>
    /// Cumulative amount refunded in <see cref="Currency"/>. This can be less
    /// than <see cref="GrossAmount"/> for a partial refund.
    /// </summary>
    public decimal RefundedAmount { get; set; }

    // ── Buyer identity (for invoicing / audit) ───────────

    /// <summary>
    /// Buyer email address
    /// </summary>
    [MaxLength(128)]
    public string BuyerEmail { get; set; }

    /// <summary>
    /// Buyer name
    /// </summary>
    [MaxLength(128)]
    public string BuyerName { get; set; }

    /// <summary>
    /// User locale at time of purchase (e.g. "en-US")
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }

    /// <summary>
    /// IP address at time of purchase (for geo-validation)
    /// </summary>
    [MaxLength(45)]
    public string IpAddress { get; set; }

    /// <summary>
    /// Whether this is a subscription renewal vs one-time purchase
    /// </summary>
    public bool IsSubscriptionPayment { get; set; }
}

/// <summary>
/// Status of a payment record
/// </summary>
public enum PaymentRecordStatus
{
    /// <summary>Payment confirmed and active</summary>
    Confirmed = 0,
    /// <summary>Payment was fully refunded</summary>
    Refunded = 1,
    /// <summary>Payment was partially refunded</summary>
    PartiallyRefunded = 2,
    /// <summary>Payment was charged back / disputed</summary>
    Disputed = 3
}
