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
using PayPalCheckoutSdk.Core;
using PayPalCheckoutSdk.Orders;
using PayPalHttp;
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
        private readonly Coflnet.Payments.Services.ProductService productService;
        private readonly IConfiguration config;
        private readonly UserService userService;
        private readonly PayPalHttpClient paypalClient;

        public TopUpController(ILogger<TopUpController> logger,
            PaymentContext context,
            ExchangeService exchangeService,
            Coflnet.Payments.Services.ProductService productService,
            IConfiguration config, UserService userService, PayPalHttpClient paypalClient)
        {
            _logger = logger;
            db = context;
            this.productService = productService;
            this.config = config;
            this.userService = userService;
            this.paypalClient = paypalClient;
        }


        /// <summary>
        /// All available topup options
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("options")]
        public async Task<List<TopUpProduct>> GetTopUps()
        {
            return await productService.GetTopupProducts();
        }

        /// <summary>
        /// Creates a payment session with stripe
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productId"></param>
        /// <param name="email">If provided, this value will be used when the Customer object is created. If not provided, customers will be asked to enter their email address</param>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<IdResponse> CreateStripeSession(string userId, string productId, string email = null)
        {
            var user = await userService.GetOrCreate(userId);
            var product = await productService.GetTopupProduct(productId);
            var eurPrice = product.Price;

            var domain = "https://sky.coflnet.com";
            var metadata = new Dictionary<string, string>() { { "productId", product.Id.ToString() } };
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

                      Currency = product.CurrencyCode,
                      //Product=productId,
                      ProductData = new SessionLineItemPriceDataProductDataOptions()
                      {
                          Name = product.Title,
                          Description = product.Description,
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
                ClientReferenceId = user.ExternalId,
                CustomerEmail = email
            };
            var service = new SessionService();
            Session session;
            try
            {
                session = await service.CreateAsync(options);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Stripe checkout session could not be created");
                throw new Exception("Payment currently unavailable");
            }

            return new IdResponse { Id = session.Id, DirctLink = session.Url };
        }
        public class IdResponse
        {
            public string Id { get; set; }
            public string DirctLink { get; set; }
        }



        /// <summary>
        /// Creates a payment session with paypal
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("paypal")]
        public async Task<IdResponse> CreatePayPal(string userId, string productId)
        {
            var user = await userService.GetOrCreate(userId);
            var product = await productService.GetTopupProduct(productId);
            var eurPrice = product.Price;
            var moneyValue = new Money() { CurrencyCode = product.CurrencyCode, Value = eurPrice.ToString("0.##") };
            var order = new OrderRequest()
            {
                CheckoutPaymentIntent = "CAPTURE",
                PurchaseUnits = new List<PurchaseUnitRequest>()
                {
                    new PurchaseUnitRequest()
                    {
                        AmountWithBreakdown = new AmountWithBreakdown()
                        {
                            CurrencyCode = product.CurrencyCode,
                            Value = eurPrice.ToString ("0.##"),
                            AmountBreakdown = new AmountBreakdown()
                            {
                                ItemTotal = moneyValue
                            }
                        },
                        CustomId = product.Id.ToString(),
                        ReferenceId = user.ExternalId,
                        Description = product.Title,
                        Items = new List<Item>(){
                            new Item(){
                                Description = product.Description,
                                Name =  product.Title,
                                Quantity = "1",
                                UnitAmount = moneyValue,

                            }
                        },
                    }
                },
                ApplicationContext = new ApplicationContext()
                {
                    ReturnUrl = "https://www.example.com",
                    CancelUrl = "https://www.example.com"
                }
            };


            // Call API with your client and get a response for your call
            var request = new OrdersCreateRequest();
            request.Prefer("return=representation");
            request.RequestBody(order);
            var response = await paypalClient.Execute(request);
            var statusCode = response.StatusCode;
            var result = response.Result<PayPalCheckoutSdk.Orders.Order>();
            Console.WriteLine("Status: {0}", result.Status);
            Console.WriteLine("Order Id: {0}", result.Id);
            Console.WriteLine("Intent: {0}", result.CheckoutPaymentIntent);
            Console.WriteLine("Links:");
            foreach (LinkDescription link in result.Links)
            {
                Console.WriteLine("\t{0}: {1}\tCall Type: {2}", link.Rel, link.Href, link.Method);
            }
            return new IdResponse(){
                DirctLink = result.Links.Where(l=>l.Rel == "approve").FirstOrDefault().Href,
                Id = result.Id
            };
        }
    }
}
