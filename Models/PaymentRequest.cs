using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

public class PaymentRequest : HasId
{
    public int Id { get; set; }
    /// <summary>
    /// The amount to pay
    /// </summary>
    /// <value></value>
    public decimal Amount { get; set; }
    /// <summary>
    /// Subscriptions: the price of every renewal when it differs from <see cref="Amount"/>
    /// (the first payment of a "first payment only" discount code). Null when all payments cost <see cref="Amount"/>.
    /// PayPal checks renewals against it; for Stripe it only keeps such a checkout from being reused for another code.
    /// </summary>
    public decimal? RecurringAmount { get; set; }
    /// <summary>
    /// Subscriptions: the buyer country the price was made for (for direct sales the verified country whose VAT
    /// <see cref="Amount"/> contains). PayPal splits the VAT of every sale with it, so a later change of the user's
    /// country does not change the VAT of an existing subscription. Null for other requests.
    /// </summary>
    [MaxLength(2)]
    public string Country { get; set; }
    /// <summary>
    /// Subscriptions: the buyer's state or province at checkout (ISO 3166-2 subdivision code without the country prefix),
    /// copied to the subscription (PayPal) or the Stripe checkout metadata, kept as evidence. Null when unknown.
    /// </summary>
    [MaxLength(3)]
    public string CheckoutRegion { get; set; }
    /// <summary>
    /// PayPal subscriptions: the creator code of the checkout, copied to the subscription on activation
    /// (creator revenue shares; Stripe keeps it in the checkout metadata)
    /// </summary>
    [MaxLength(50)]
    public string CreatorCode { get; set; }
    /// <summary>
    /// PayPal subscriptions: the local discount code of the checkout, copied to the subscription on activation
    /// </summary>
    [MaxLength(50)]
    public string DiscountCode { get; set; }
    /// <summary>
    /// Subscriptions: the checkout is sold through Stripe Managed Payments (Stripe collects the tax) instead of directly.
    /// Both can have the same amount (US), so an open checkout is only reused for the same mode.
    /// </summary>
    public bool ManagedPayments { get; set; }
    /// <summary>
    /// The coins this request buys. Null for requests created before the column existed;
    /// providers that fulfil from this record require it.
    /// </summary>
    public decimal? CoinAmount { get; set; }
    /// <summary>
    /// The id of the product to pay for
    /// </summary>
    /// <value></value>
    public TopUpProduct ProductId { get; set; }
    /// <summary>
    /// The id of the user to pay for
    /// </summary>
    /// <value></value>
    public User User { get; set; }
    /// <summary>
    /// The slug of the payment provider
    /// </summary>
    /// <value></value>
    [MaxLength(32)]
    public string Provider { get; set; }
    /// <summary>
    /// Session id from the payment provider
    /// </summary>
    [MaxLength(75)]
    public string SessionId { get; set; }
    /// <summary>
    /// The device ip the session was created with
    /// </summary>
    public System.Net.IPAddress CreateOnIp { get; set; }
    /// <summary>
    /// Locale of the device the session was created with
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }
    /// <summary>
    /// Device Fingerprint of the device the session was created with
    /// </summary>
    [MaxLength(32)]
    public string DeviceFingerprint { get; set; }
    /// <summary>
    /// The current status of the payment request
    /// </summary>
    public Status State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>
    /// Subscription checkouts priced with a discount code that expires: the code's expiry. Checkouts started before it
    /// stay payable for 24 hours after it (Stripe's session lifetime); a PayPal subscription approved later than that
    /// is cancelled and not granted.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>
    /// States a payment request can be in
    /// </summary>
    public enum Status
    {
        /// <summary>
        /// The payment request was created
        /// </summary>
        CREATED,
        /// <summary>
        /// The payment request was confirmed
        /// </summary>
        CONFIRMED,
        /// <summary>
        /// The payment request was rejected
        /// </summary>
        REJECTED,
        /// <summary>
        /// Waiting for the user to enter payment details
        /// </summary>
        WAITING,
        /// <summary>
        /// Payment is being processed
        /// </summary>
        PROCESSING,
        /// <summary>
        /// The payment request was paid
        /// </summary>
        PAID,
        /// <summary>
        /// The payment request was canceled
        /// </summary>
        CANCELED,
        /// <summary>
        /// The payment request was refunded
        /// </summary>
        REFUNDED,
        /// <summary>
        /// The payment request was expired
        /// </summary>
        EXPIRED,
        /// <summary>
        /// The payment request has failed
        /// </summary>
        FAILED,
        /// <summary>
        /// The payment was captured but requires a manual refund
        /// </summary>
        REFUND_PENDING,
    }
}
