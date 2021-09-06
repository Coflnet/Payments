using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

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
        public async Task<IdResponse> CreateStripeSession(string userId, string productId)
        {
            var product = GetProduct(productId);
            var price = GetPrice(productId);

            var domain = "https://sky.coflnet.com";
            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string>
                {
                  "card",
                },
                LineItems = new List<SessionLineItemOptions>
                {
                  new SessionLineItemOptions
                  {

                    PriceData = new SessionLineItemPriceDataOptions
                    {
                      UnitAmount = price.UnitAmount,

                      Currency = "eur",
                      Product=productId
                    },

                   // Description = "Unlocks premium features: Subscribe to 100 Thrings, Search with multiple filters and you support the project :)",
                    Quantity = 1,
                  },
                },
                Metadata = product.Metadata,
                Mode = "payment",
                SuccessUrl = domain + "/success",
                CancelUrl = domain + "/cancel",
                ClientReferenceId = userId
            };
            var service = new SessionService();
            Session session;
            try
            {
                session = service.Create(options);
            }
            catch (Exception e)
            {
                _logger.LogError(e,"Stripe checkout session could not be created");
                throw new Exception("Payment current unavailable");
            }

            return new IdResponse{ Id = session.Id };
        }
        public class IdResponse 
        {
            public string Id {get;set;}
        }

        Dictionary<string,Price> priceCache = null;

        public Price GetPrice(string productId)
        {
            var service = new PriceService();
            if(priceCache == null)
            {
                priceCache = service.List().ToDictionary(e=>e.ProductId);
                _logger.LogInformation(Newtonsoft.Json.JsonConvert.SerializeObject(priceCache));
            }
            if(priceCache.TryGetValue(productId,out Price value))
                return value;

            throw new System.Exception($"The price for id {productId} was not found");
        }
        Dictionary<string,Product> productCache = null;

        public Product GetProduct(string productId)
        {
            var service = new ProductService();
            if(priceCache == null)
            {
                productCache = service.List().ToDictionary(e=>e.Id);
                _logger.LogInformation(Newtonsoft.Json.JsonConvert.SerializeObject(priceCache));
            }
            if(productCache.TryGetValue(productId,out Product value))
                return value;

            throw new System.Exception($"The product with id {productId} was not found");
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
