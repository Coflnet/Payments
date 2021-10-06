using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Manages products
    /// </summary>
    public class ProductService
    {
        private ILogger<ProductService> logger;
        private PaymentContext db;

        /// <summary>
        /// Instantiates a new instance of the <see cref="ProductService"/>
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="context"></param>
        public ProductService(
            ILogger<ProductService> logger,
            PaymentContext context)
        {
            this.logger = logger;
            db = context;
        }

        /// <summary>
        /// Get a product by slug
        /// </summary>
        /// <param name="slug"></param>
        /// <returns></returns>
        public async Task<PurchaseableProduct> GetProduct (string slug)
        {
            var product = await db.Products.Where(p => p.Slug == slug).FirstOrDefaultAsync();
            if(product == null)
                throw new System.Exception($"product with slug '{slug}' not found");
            return product;
        }
    }
}