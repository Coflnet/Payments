using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
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

        [HttpGet]
        [Route("")]
        public async Task<IEnumerable<PurchaseableProduct>> Get(string userId)
        {
            return await db.Products.ToListAsync();
        }

        [HttpPost]
        [Route("")]
        public async Task<PurchaseableProduct> CreateNew(PurchaseableProduct product)
        {
            db.Add(product);
            await db.SaveChangesAsync();
            return await db.Products.Where(p=>p.Slug == product.Slug).FirstOrDefaultAsync();
        }
    }
}
