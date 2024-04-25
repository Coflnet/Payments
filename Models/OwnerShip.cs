
using System;
using Newtonsoft.Json;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Represents an ownership of some product by some user.
    /// Has the time of when the ownership expires.
    /// </summary>
    public class OwnerShip : HasLongId
    {
        /// <summary>
        /// Primary Id
        /// </summary>
        /// <value></value>
        public long Id { get; set; }
        /// <summary>
        /// The user having the ownership
        /// </summary>
        /// <value></value>
        [JsonIgnore]
        public User User { get; set; }
        /// <summary>
        /// The produt being owned
        /// </summary>
        /// <value></value>
        public PurchaseableProduct Product { get; set; }
        /// <summary>
        /// How long 
        /// </summary>
        /// <value></value>
        public DateTime Expires { get; set; }
    }
}