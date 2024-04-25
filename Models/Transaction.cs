using System;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Base class for transactions
    /// </summary>
    public class Transaction : HasLongId
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
        [JsonIgnore]
        public User User { get; set; }
        /// <summary>
        /// What product this transaction coresponds to (gives context why this happened)
        /// </summary>
        /// <value></value>
        public Product Product { get; set; }
        /// <summary>
        /// The size of the transaction
        /// </summary>
        /// <value></value>
        public decimal Amount { get; set; }
        /// <summary>
        /// Custom reference data for this transaction.
        /// External identifiers, notes, metadata
        /// </summary>
        /// <value></value>
        [MaxLength(80)]
        public string Reference { get; set; }
        /// <summary>
        /// Timestamp of this transaction
        /// </summary>
        /// <value></value>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    }
}