namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Transfer request to another user
    /// </summary>
    public class TransferRequest
    {
        /// <summary>
        /// The identifier of the user which should receive the funds
        /// </summary>
        /// <value></value>
        public string TargetUser { get; set; }
        /// <summary>
        /// A unique reference to prevent double transfers
        /// </summary>
        /// <value></value>
        public string Reference { get; set; }
        /// <summary>
        /// The amount to transfer
        /// </summary>
        /// <value></value>
        public double Amount { get; set; }
    }
}