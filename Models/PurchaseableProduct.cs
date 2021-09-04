namespace Coflnet.Payments.Models
{
    /// <summary>
    /// Products that can be purchased
    /// </summary>
    public class PurchaseableProduct
    {
        public int Id { get; set; }
        public string Title { get; set; }
        /// <summary>
        /// unique Slug for this product
        /// </summary>
        /// <value></value>
        public string Slug { get; set; }
        /// <summary>
        /// 
        /// </summary>
        /// <value></value>
        public string Description { get; set; }
        /// <summary>
        /// The minimum amount this product costs to purchase
        /// </summary>
        /// <value></value>
        public decimal Cost { get; set; }
        /// <summary>
        /// How long this product is owned by an user in seconds
        /// </summary>
        /// <value></value>
        public long OwnershipSeconds { get; set; }
        /// <summary>
        /// Wherever this product can be purchased or not
        /// </summary>
        public bool IsDisabled { get; set; }
    }
}