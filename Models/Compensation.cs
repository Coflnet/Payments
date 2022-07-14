using System;

namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Compensation for some kind of incident 
    /// </summary>
    public class Compensation
    {
        /// <summary>
        /// The product id owners of which to compensate
        /// </summary>
        /// <value></value>
        public string ProductId { get; set; }
        /// <summary>
        /// A reason for the compensation, also serves as double compensation prevention
        /// </summary>
        /// <value></value>
        public string Reference { get; set; }
        /// <summary>
        /// How much to compensate
        /// </summary>
        /// <value></value>
        public int Amount { get; set; }
        /// <summary>
        /// At what time ownership of <see cref="Product"/> should be looked for (services might expired since)
        /// </summary>
        /// <value></value>
        public DateTime When { get; set; }
    }
}