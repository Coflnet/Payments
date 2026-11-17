using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Detailed export view of a single transaction for compliance and reporting.
    /// Maps from PaymentRecord for API responses.
    /// </summary>
    public class DetailedTransactionExport
    {
        /// <summary>
        /// Transaction ID
        /// </summary>
        public string TransactionId { get; set; }

        /// <summary>
        /// User ID who made the payment
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// User's country (ISO 3166-1 alpha-2)
        /// </summary>
        public string UserCountry { get; set; }

        /// <summary>
        /// User's ZIP / postal code
        /// </summary>
        public string ZipCode { get; set; }

        /// <summary>
        /// User's state / province
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// Payment provider slug (e.g., "coingate", "stripe", "googlepay")
        /// </summary>
        public string Provider { get; set; }

        /// <summary>
        /// Gross amount charged to customer
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Subtotal before tax
        /// </summary>
        public decimal Subtotal { get; set; }

        /// <summary>
        /// Discount applied by processor
        /// </summary>
        public decimal DiscountAmount { get; set; }

        /// <summary>
        /// Tax collected by processor
        /// </summary>
        public decimal TaxAmount { get; set; }

        /// <summary>
        /// Tax rate percentage
        /// </summary>
        public double TaxRate { get; set; }

        /// <summary>
        /// Whether the processor already remitted tax
        /// </summary>
        public bool TaxRemittedByProcessor { get; set; }

        /// <summary>
        /// Net amount received
        /// </summary>
        public decimal NetAmount { get; set; }

        /// <summary>
        /// Processor fee
        /// </summary>
        public decimal ProcessorFee { get; set; }

        /// <summary>
        /// Creator code used (if any)
        /// </summary>
        public string CreatorCode { get; set; }

        /// <summary>
        /// Creator code discount
        /// </summary>
        public decimal CreatorCodeDiscount { get; set; }

        /// <summary>
        /// Currency code (ISO 4217)
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// Product purchased (e.g., product slug)
        /// </summary>
        public string ProductId { get; set; }

        /// <summary>
        /// Virtual currency amount credited
        /// </summary>
        public long CoinAmount { get; set; }

        /// <summary>
        /// Date and time of payment (UTC)
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Payment record status
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// External order ID from payment provider
        /// </summary>
        public string ExternalReference { get; set; }

        /// <summary>
        /// Secondary external transaction ID
        /// </summary>
        public string ExternalTransactionId { get; set; }

        /// <summary>
        /// ISO 639-1 locale of the payment session
        /// </summary>
        public string Locale { get; set; }

        /// <summary>
        /// Payment method (card, paypal, crypto, etc.)
        /// </summary>
        public string PaymentMethod { get; set; }

        /// <summary>
        /// Whether this is a subscription payment
        /// </summary>
        public bool IsSubscriptionPayment { get; set; }

        /// <summary>
        /// Buyer email
        /// </summary>
        public string BuyerEmail { get; set; }

        /// <summary>
        /// Subscription payments: the country whose VAT the price contains (place of supply evidence)
        /// </summary>
        public string CheckoutCountry { get; set; }

        /// <summary>
        /// Stripe: country of the billing address entered at checkout
        /// </summary>
        public string BillingCountry { get; set; }

        /// <summary>
        /// Stripe: issuing country of the card
        /// </summary>
        public string PaymentMethodCountry { get; set; }

        /// <summary>
        /// PayPal: the payer's country when PayPal returns one (card funded subscriptions only), otherwise empty
        /// </summary>
        public string PayerCountry { get; set; }

        /// <summary>
        /// IP address of the checkout request
        /// </summary>
        public string IpAddress { get; set; }

        /// <summary>
        /// "direct" (Stripe/PayPal, Coflnet remits the VAT), "managed" (Stripe Managed Payments), the provider for other subscription
        /// payments ("lemonsqueezy", "googlepay") or empty (not a subscription payment)
        /// </summary>
        public string SubscriptionMode { get; set; }

        /// <summary>
        /// Subscription payments: the buyer's state or province at checkout (ISO 3166-2 subdivision code without country prefix)
        /// </summary>
        public string CheckoutRegion { get; set; }
    }

    /// <summary>
    /// The Status values of the revenue export. Payment records map by their <see cref="PaymentRecordStatus"/> name
    /// (Confirmed, Refunded, PartiallyRefunded, Disputed); payments that have no record are mapped from the
    /// <see cref="PaymentRequest.Status"/> they were left in: REFUND_PENDING to RefundPending, REJECTED to Rejected.
    /// </summary>
    public static class ExportStatus
    {
        public const string RefundPending = "RefundPending";
        public const string Rejected = "Rejected";

        public static string FromRequest(PaymentRequest.Status status) => status switch
        {
            PaymentRequest.Status.REFUND_PENDING => RefundPending,
            PaymentRequest.Status.REJECTED => Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Only unfulfilled payments are exported by request status")
        };
    }
}
