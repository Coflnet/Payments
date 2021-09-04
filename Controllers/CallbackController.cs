using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// External server side callbacks
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class CallbackController : ControllerBase
    {
        private readonly ILogger<CallbackController> _logger;
        private readonly PaymentContext db;

        public CallbackController(ILogger<CallbackController> logger, PaymentContext context)
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

    }
}
