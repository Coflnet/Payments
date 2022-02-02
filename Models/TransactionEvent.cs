using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Event produced when a transaction occurs
    /// </summary>
    public class TransactionEvent
    {
        /// <summary>
        /// The internal id of the transaction
        /// </summary>
        public long Id { get; set; }
        /// <summary>
        /// Id of the user triggering the transaction
        /// </summary>
        public string UserId { get; set; }
        /// <summary>
        /// Slug of the product
        /// </summary>
        public string ProductSlug { get; set; }
        /// <summary>
        /// Unique id of this product settings
        /// </summary>
        public int ProductId { get; set; }
        /// <summary>
        /// How long this product will last till it expires
        /// </summary>
        public long OwnedSeconds { get; set; }
        /// <summary>
        /// The transaction amount
        /// </summary>
        public double Amount { get; set; }
        /// <summary>
        /// Optional reference
        /// </summary>
        public string Reference { get; set; }
        /// <summary>
        /// When this transaction occured
        /// </summary>
        public DateTime Timestamp { get; set; }
    }
}