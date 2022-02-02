using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models
{
    public class TopUpProduct : Product
    {
        /// <summary>
        /// The price of this <see cref="TopUpProduct"/> in <see cref="CurrencyCode"/> 
        /// </summary>
        /// <value></value>
        public decimal Price { get; set; }
        /// <summary>
        /// The currency code 
        /// </summary>
        /// <value></value>
        [MaxLength(3)]
        public string CurrencyCode { get; set; }
        /// <summary>
        /// What provider this top up is valid for 
        /// (differnt fees can require different prices)
        /// </summary>
        /// <value></value>
        [MaxLength(16)]
        public string ProviderSlug { get; set; }
    }
}