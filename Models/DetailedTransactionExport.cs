using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Detailed export view of a single transaction for compliance and reporting
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
        /// Payment provider slug (e.g., "coingate", "stripe", "googlepay")
        /// </summary>
        public string Provider { get; set; }

        /// <summary>
        /// Amount paid
        /// </summary>
        public decimal Amount { get; set; }

        /// <summary>
        /// Currency code (ISO 4217)
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// Product purchased (e.g., product slug)
        /// </summary>
        public string ProductId { get; set; }

        /// <summary>
        /// Date and time of transaction (UTC)
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Payment request status at time of export
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// External reference/session ID from payment provider
        /// </summary>
        public string ExternalReference { get; set; }

        /// <summary>
        /// ISO 639-1 locale of the payment session
        /// </summary>
        public string Locale { get; set; }
    }
}
