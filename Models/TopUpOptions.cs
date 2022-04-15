namespace Coflnet.Payments.Models
{
    public class TopUpOptions
    {
        /// <summary>
        /// Overwrite the default redirect url after successful payment
        /// </summary>
        /// <value></value>
        public string SuccessUrl { get; set; }
        /// <summary>
        /// Overwrite the default redirect url for anything else but payment
        /// </summary>
        /// <value></value>
        public string CancelUrl { get; set; }
        /// <summary>
        /// If provided, this value will be used when the Customer object is created. If not provided, customers will be asked to enter their email address
        /// </summary>
        /// <value></value>
        public string UserEmail { get; set; }
        /// <summary>
        /// Percise amount of coflcoins to topup
        /// </summary>
        /// <value></value>
        public long TopUpAmount { get; set; }
    }
}