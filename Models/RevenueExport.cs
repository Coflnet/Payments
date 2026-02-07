using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Represents aggregated revenue data for export
    /// Used for compliance reporting of payments by country, provider, and time period
    /// </summary>
    public class RevenueExport
    {
        /// <summary>
        /// Country code (ISO 3166-1 alpha-2) where the payment originated
        /// </summary>
        public string Country { get; set; }

        /// <summary>
        /// Payment provider slug (e.g., "coingate", "stripe", "googlepay", etc.)
        /// </summary>
        public string Provider { get; set; }

        /// <summary>
        /// Total amount in this category
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Number of transactions in this category
        /// </summary>
        public int TransactionCount { get; set; }

        /// <summary>
        /// Currency code (ISO 4217)
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// Start of the period this data covers
        /// </summary>
        public DateTime PeriodStart { get; set; }

        /// <summary>
        /// End of the period this data covers
        /// </summary>
        public DateTime PeriodEnd { get; set; }
    }
}
