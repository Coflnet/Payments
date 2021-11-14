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
        public long Id;
        /// <summary>
        /// Id of the user triggering the transaction
        /// </summary>
        public string UserId;
        /// <summary>
        /// Slug of the product
        /// </summary>
        public string ProductSlug;
        /// <summary>
        /// Unique id of this product settings
        /// </summary>
        public int ProductId;
        /// <summary>
        /// How long this product will last till it expires
        /// </summary>
        public long OwnedSeconds;
        /// <summary>
        /// The transaction amount
        /// </summary>
        public double Amount;
        /// <summary>
        /// Optional reference
        /// </summary>
        public string Reference;
        /// <summary>
        /// When this transaction occured
        /// </summary>
        public DateTime Timestamp;
    }
}