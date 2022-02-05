namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Represents a custom adjustment to an users balance 
    /// </summary>
    public class CustomTopUp
    {
        /// <summary>
        /// What product this coresponds to (the product can define additional information)
        /// </summary>
        /// <value></value>
        public string ProductId { get; set; }
        /// <summary>
        /// How much to add/substract
        /// </summary>
        /// <value></value>
        public long Amount { get; set; }
        /// <summary>
        /// Reference/Reason this topup took place, dupplicates will be rejected
        /// </summary>
        /// <value></value>
        public string Reference { get; set; }
    }
}