using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models
{
    public class Product
    {
        /// <summary>
        /// Primary Key
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Title of this product 
        /// </summary>
        /// <value></value>
        [MaxLength(80)]
        public string Title { get; set; }
        /// <summary>
        /// unique Slug for this product
        /// </summary>
        /// <value></value>
        [MaxLength(32)]
        public string Slug { get; set; }
        /// <summary>
        /// DB-Level description may not be displayed to the end user directly (localisation n stuff)
        /// </summary>
        /// <value></value>
        public string Description { get; set; }
        /// <summary>
        /// The exact amount this product costs to purchase
        /// </summary>
        /// <value></value>
        public decimal Cost { get; set; }
        /// <summary>
        /// How long this product is owned by an user in seconds
        /// </summary>
        /// <value></value>
        public long OwnershipSeconds { get; set; }
        /// <summary>
        /// The type of this product <see cref="ProductType"/>
        /// </summary>
        /// <value></value>
        public ProductType Type { get; set; }

        /// <summary>
        /// Types of products
        /// </summary>
        public enum ProductType
        {
            /// <summary>
            /// No special settings, handled as normal product
            /// </summary> 
            NONE,
            /// <summary>
            /// A service that expires after some time
            /// </summary> 
            SERVICE,
            /// <summary>
            /// A 'normal' product
            /// </summary> 
            COLLECTABLE,
            /// <summary>
            /// Causes the products Cost to invert
            /// </summary> 
            TOP_UP = 4,
            /// <summary>
            /// Products with this flag just lock some amount and are not able to become permanent
            /// </summary> 
            LOCKED = 8,
            /// <summary>
            /// Products with this flag just lock some amount and are not able to become permanent
            /// </summary> 
            DISABLED = 16,
            /// <summary>
            /// The <see cref="Cost"/> is the minimal cost but can be increased
            /// </summary> 
            VARIABLE_PRICE = 32
        }
    }
}