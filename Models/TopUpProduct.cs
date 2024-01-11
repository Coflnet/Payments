using System;
using System.Collections.Generic;
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

        /// <summary>
        /// copy constructor
        /// </summary>
        /// <param name="product"></param>
        public TopUpProduct(TopUpProduct product) : base(product)
        {
            Price = product.Price;
            CurrencyCode = product.CurrencyCode;
            ProviderSlug = product.ProviderSlug;
        }

        public TopUpProduct()
        {
        }

        public override bool Equals(object obj)
        {
            return obj is TopUpProduct product &&
                   base.Equals(obj) &&
                   Title == product.Title &&
                   Slug == product.Slug &&
                   Description == product.Description &&
                   Cost == product.Cost &&
                   OwnershipSeconds == product.OwnershipSeconds &&
                   Type == product.Type &&
                   Price == product.Price &&
                   CurrencyCode == product.CurrencyCode &&
                   ProviderSlug == product.ProviderSlug;
        }

        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(base.GetHashCode());
            hash.Add(Title);
            hash.Add(Slug);
            hash.Add(Description);
            hash.Add(Cost);
            hash.Add(OwnershipSeconds);
            hash.Add(Type);
            hash.Add(Price);
            hash.Add(CurrencyCode);
            hash.Add(ProviderSlug);
            return hash.ToHashCode();
        }
    }
}