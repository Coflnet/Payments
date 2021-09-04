using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// Handles creating top up requests and contacting external apis
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class TopUpController : ControllerBase
    {
        private readonly ILogger<TopUpController> _logger;
        private readonly PaymentContext db;

        public TopUpController(ILogger<TopUpController> logger, PaymentContext context)
        {
            _logger = logger;
            db = context;
        }

        /// <summary>
        /// Creates a payment session with stripe
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<IEnumerable<PurchaseableProduct>> CreateStripe(string userId)
        {
            return await db.Products.ToListAsync();
        }

        /// <summary>
        /// Creates a payment session with stripe
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("paypal")]
        public async Task<IEnumerable<PurchaseableProduct>> CreatePayPal(string userId)
        {
            return await db.Products.ToListAsync();
        }
    }
}
