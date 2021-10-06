using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        private readonly ExchangeService exchangeService;
        private readonly Coflnet.Payments.Services.ProductService productService;

        public TopUpController(ILogger<TopUpController> logger,
            PaymentContext context,
            ExchangeService exchangeService,
            Coflnet.Payments.Services.ProductService productService)
        {
            _logger = logger;
            db = context;
            this.exchangeService = exchangeService;
            this.productService = productService;
        }

        /// <summary>
        /// Creates a payment session with stripe
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<IdResponse> CreateStripeSession(string userId, string productId)
        {
            var product = await productService.GetProduct(productId);
            var eurPrice = exchangeService.ToEur(product.Cost);

            var domain = "https://sky.coflnet.com";
            var metadata = new Dictionary<string, string>(){{"productId",product.Id.ToString()}};
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
                      UnitAmount = (long)(eurPrice * 100),

                      Currency = "eur",
                      //Product=productId,
                      ProductData = new SessionLineItemPriceDataProductDataOptions()
                      {
                          Name = product.Title,
                          Metadata = metadata
                      }
                    },

                   // Description = "Unlocks premium features: Subscribe to 100 Thrings, Search with multiple filters and you support the project :)",
                    Quantity = 1,
                  },
                },
                Metadata = metadata,
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
                _logger.LogError(e, "Stripe checkout session could not be created");
                throw new Exception("Payment currently unavailable");
            }

            return new IdResponse { Id = session.Id };
        }
        public class IdResponse
        {
            public string Id { get; set; }
        }



        /// <summary>
        /// Creates a payment session with paypal
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("paypal")]
        public async Task<IEnumerable<PurchaseableProduct>> CreatePayPal(string userId)
        {
            throw new NotImplementedException("todo ...");
        }
    }
}
