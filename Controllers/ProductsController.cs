using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ProductsController : ControllerBase
    {
        private readonly ILogger<ProductsController> _logger;
        private readonly PaymentContext db;
        private readonly ProductService productService;

        public ProductsController(ILogger<ProductsController> logger, PaymentContext context, ProductService productService)
        {
            _logger = logger;
            db = context;
            this.productService = productService;
        }

        /// <summary>
        /// Get the details of a product
        /// </summary>
        /// <param name="productSlug"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("p/{productSlug}")]
        public Task<PurchaseableProduct> Get(string productSlug)
        {
            return db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Get all products
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("")]
        public async Task<IEnumerable<PurchaseableProduct>> GetAll(int offset = 0, int amount = 20)
        {
            return await db.Products.Skip(offset).Take(amount).ToListAsync();
        }

        /// <summary>
        /// Get topup options
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("topup")]
        public async Task<IEnumerable<TopUpProduct>> GetTopupOptions(int offset = 0, int amount = 20)
        {
            return await db.TopUpProducts.Skip(offset).Take(amount).ToListAsync();
        }


        /// <summary>
        /// Get services
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("services")]
        public async Task<IEnumerable<PurchaseableProduct>> GetServices(int offset = 0, int amount = 20)
        {
            return await db.Products.Where(p => p.Type.HasFlag(PurchaseableProduct.ProductType.SERVICE)).Skip(offset).Take(amount).ToListAsync();
        }

        /// <summary>
        /// Updates a product by replacing it with a new one.
        /// Old products can not be deleted to furfill accounting needs 
        /// </summary>
        /// <param name="product">the product to update</param>
        /// <returns></returns>
        [HttpPut]
        [Route("")]
        public async Task<PurchaseableProduct> UpdateProduct(PurchaseableProduct product)
        {
            return await productService.UpdateOrAddProduct(product);
        }

        /// <summary>
        /// Updates a topup option by replacing it with a new one.
        /// Old options get a new slug and are marked as disabled
        /// </summary>
        /// <param name="product"></param>
        /// <returns></returns>
        [HttpPut]
        [Route("topup")]
        public async Task<TopUpProduct> UpdateTopUpProduct(TopUpProduct product)
        {
            return await productService.UpdateTopUpProduct(product);
        }
    }
}
