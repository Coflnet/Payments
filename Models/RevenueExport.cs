using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Represents aggregated revenue data for export.
    /// Now sourced from PaymentRecords for accurate tax compliance.
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
        /// Total gross amount charged to customers
        /// </summary>
        public decimal TotalAmount { get; set; }

        /// <summary>
        /// Total tax collected by processors
        /// </summary>
        public decimal TotalTax { get; set; }

        /// <summary>
        /// Total net amount (gross − tax − fees)
        /// </summary>
        public decimal TotalNet { get; set; }

        /// <summary>
        /// Total processor fees
        /// </summary>
        public decimal TotalFees { get; set; }

        /// <summary>
        /// Total discount amount
        /// </summary>
        public decimal TotalDiscount { get; set; }

        /// <summary>
        /// Whether tax was remitted by the processor for this group
        /// </summary>
        public bool TaxRemittedByProcessor { get; set; }

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
