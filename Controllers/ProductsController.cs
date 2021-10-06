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

        public ProductsController(ILogger<ProductsController> logger, PaymentContext context)
        {
            _logger = logger;
            db = context;
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
        public async Task<IEnumerable<PurchaseableProduct>> GetTopupOptions(int offset = 0, int amount = 20)
        {
            return await db.Products.Where(p=>p.Type.HasFlag(PurchaseableProduct.ProductType.TOP_UP)).Skip(offset).Take(amount).ToListAsync();
        }


        /// <summary>
        /// Get services
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("services")]
        public async Task<IEnumerable<PurchaseableProduct>> GetServices(int offset = 0, int amount = 20)
        {
            return await db.Products.Where(p=>p.Type.HasFlag(PurchaseableProduct.ProductType.SERVICE)).Skip(offset).Take(amount).ToListAsync();
        }

        [HttpPost]
        [Route("")]
        public async Task<PurchaseableProduct> CreateNew(PurchaseableProduct product)
        {
            db.Add(product);
            await db.SaveChangesAsync();
            return await GetProduct(product);
        }

        private async Task<PurchaseableProduct> GetProduct(PurchaseableProduct product)
        {
            return await db.Products.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
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
            var oldProduct = await GetProduct(product);
            if (oldProduct != null)
            {
                // change the old slug
                var newSlug = oldProduct.Slug.Truncate(18) + Convert.ToBase64String(BitConverter.GetBytes(DateTime.Now.Ticks)).Reverse();
                oldProduct.Slug = newSlug.Truncate(20);
                db.Update(oldProduct);
            }

            db.Add(product);
            await db.SaveChangesAsync();
            return await db.Products.Where(p => p.Slug == product.Slug).FirstOrDefaultAsync();
        }
    }
}
