namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Settings for coin transfers
    /// </summary>
    public class TransferSettings
    {
        /// <summary>
        /// How often a transfer is allowed to take place
        /// </summary>
        /// <value></value>
        public int Limit { get; set; }
        /// <summary>
        /// Howmany days have to pass before the a transaction is removed from counting against the limit
        /// </summary>
        /// <value></value>
        public double PeriodDays { get; set; }
    }
}