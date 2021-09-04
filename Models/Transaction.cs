using System;

namespace Coflnet.Payments.Models
{
    public class Transaction
    {
        /// <summary>
        /// Primary Id
        /// </summary>
        /// <value></value>
        public long Id { get; set; }
        /// <summary>
        /// The user doing the transaction
        /// </summary>
        /// <value></value>
        public User User { get; set; }
        /// <summary>
        /// What product this transaction coresponds to (gives context why this happened)
        /// </summary>
        /// <value></value>
        public PurchaseableProduct Product { get; set; }
        /// <summary>
        /// The size of the transaction
        /// </summary>
        /// <value></value>
        public decimal Amount { get; set; }
        /// <summary>
        /// Timestamp of this transaction
        /// </summary>
        /// <value></value>
        public DateTime Timestamp { get; set; } = DateTime.Now;

    }
}