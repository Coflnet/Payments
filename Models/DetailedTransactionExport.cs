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
    }
}
