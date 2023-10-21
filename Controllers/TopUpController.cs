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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayPalCheckoutSdk.Core;
using PayPalCheckoutSdk.Orders;
using PayPalHttp;
using RestSharp;
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
        private readonly Coflnet.Payments.Services.TransactionService transactionService;
        private readonly IConfiguration config;
        private readonly UserService userService;
        private readonly PayPalHttpClient paypalClient;

        /// <summary>
        /// Creates a new instance of the <see cref="TopUpController"/> class
        /// </summary>
        public TopUpController(ILogger<TopUpController> logger,
            PaymentContext context,
            Coflnet.Payments.Services.ProductService productService,
            Coflnet.Payments.Services.TransactionService transactionService,
            IConfiguration config,
            UserService userService,
            PayPalHttpClient paypalClient)
        {
            _logger = logger;
            db = context;
            this.productService = productService;
            this.config = config;
            this.userService = userService;
            this.paypalClient = paypalClient;
            this.transactionService = transactionService;
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
        /// <param name="topupotions">Additional options</param>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<TopUpIdResponse> CreateStripeSession(string userId, string productId, [FromBody] TopUpOptions topupotions = null)
        {
            var user = await userService.GetOrCreate(userId);
            AssertUserCountry(topupotions);
            var product = await GetTopupProduct(productId, "stripe");
            if (product == null)
                throw new ApiException("Product not found");

            GetPriceAndCoins(topupotions, product, out decimal eurPrice, out decimal coinAmount);

            var instance = await AttemptBlockFraud(topupotions, user, product, eurPrice);

            if (user.Locale == null && topupotions?.UserIp != null)
            {
                user.Locale = topupotions?.Locale;
                user.Ip = System.Net.IPAddress.Parse(topupotions.UserIp).ToString();
                user.Country = topupotions?.Locale.Split('-').Last();
                await db.SaveChangesAsync();
            }

            var metadata = new Dictionary<string, string>() {
                { "productId", product.Id.ToString() },
                { "coinAmount", coinAmount.ToString() } };
            var options = new SessionCreateOptions
            {
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
                    Quantity = 1,
                  },
                },
                Metadata = metadata,
                Mode = "payment",
                SuccessUrl = topupotions?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"],
                CancelUrl = topupotions?.CancelUrl ?? config["DEFAULT:CANCEL_URL"],
                ClientReferenceId = user.ExternalId,
                CustomerEmail = topupotions?.UserEmail
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
            instance.SessionId = session.Id;
            await db.SaveChangesAsync();

            return new TopUpIdResponse { Id = session.Id, DirctLink = session.Url };
        }

        private static void AssertUserCountry(TopUpOptions topupotions)
        {
            if (!CallbackController.DoWeSellto(topupotions.Locale, null))
                throw new ApiException($"We are sorry but we can not sell to your country ({topupotions.Locale}) at this time");
        }

        private async Task<PaymentRequest> AttemptBlockFraud(TopUpOptions topupotions, User user, TopUpProduct product, decimal eurPrice)
        {
            using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (!System.Net.IPAddress.TryParse(topupotions.UserIp, out var userIp))
                throw new ApiException("Invalid user IP");
            // check existing requests 
            var minTime = DateTime.UtcNow.AddHours(-10);
            var existingRequests = await db.PaymentRequests
                .Where(r => (r.User.Id == user.Id || r.CreateOnIp == userIp) && r.CreatedAt > minTime)
                .Where(r => r.State >= PaymentRequest.Status.CREATED && r.State < PaymentRequest.Status.PAID)
                .ToListAsync();
            if (existingRequests.Count(r => r.CreatedAt >= DateTime.UtcNow.AddMinutes(-10)) > 1)
                throw new ApiException("Too many payment requests from you, please try again later");
            if (existingRequests.Count > 5 || existingRequests.Count > 3 && existingRequests.Select(e => e.CreateOnIp).Distinct().Count() < 3)
                throw new ApiException($"Too many payment requests from you, please ask for support on discord or email support@coflnet.com with {topupotions?.Fingerprint?.Substring(0, 5)}");

            var request = new PaymentRequest()
            {
                User = user,
                ProductId = product,
                CreatedAt = DateTime.UtcNow,
                State = PaymentRequest.Status.CREATED,
                Amount = eurPrice,
                CreateOnIp = userIp,
                DeviceFingerprint = topupotions.Fingerprint,
                Locale = topupotions.Locale,
                Provider = product.ProviderSlug
            };
            db.Add(request);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            _logger.LogInformation("Created payment request {requestId} for user {userId}, existing {existingCount}", request.Id, user.Id, existingRequests.Count);
            return request;
        }


        /// <summary>
        /// Creates a payment session with paypal
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productId"></param>
        /// <param name="options">Additional options</param>
        /// <returns></returns>
        [HttpPost]
        [Route("paypal")]
        public async Task<TopUpIdResponse> CreatePayPal(string userId, string productId, [FromBody] TopUpOptions options = null)
        {
            var user = await userService.GetOrCreate(userId);
            AssertUserCountry(options);
            var product = await GetTopupProduct(productId, "paypal");
            GetPriceAndCoins(options, product, out decimal eurPrice, out decimal coinAmount);
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
                        CustomId = product.Id.ToString() + ";" + coinAmount  + ";" + user.ExternalId,
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
                    ReturnUrl = options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"],
                    CancelUrl = options?.CancelUrl ?? config["DEFAULT:CANCEL_URL"]
                },
                Payer = new Payer()
                {
                    Email = options?.UserEmail
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

            return new TopUpIdResponse()
            {
                DirctLink = result.Links.Where(l => l.Rel == "approve").FirstOrDefault().Href,
                Id = result.Id
            };
        }

        [HttpPost]
        [Route("lemonsqueezy")]
        public async Task<TopUpIdResponse> CreateLemonSqueezy(string userId, string productId, [FromBody] TopUpOptions options = null)
        {
            var user = await userService.GetOrCreate(userId);
            TopUpProduct product = await GetTopupProduct(productId, "lemonsqueezy");
            GetPriceAndCoins(options, product, out decimal eurPrice, out decimal coinAmount);
            var moneyValue = new Money() { CurrencyCode = product.CurrencyCode, Value = eurPrice.ToString("0.##") };

            var restclient = new RestClient("https://api.lemonsqueezy.com/v1/checkouts");
            var request = new RestRequest("", Method.Post);
            request.AddHeader("Accept", "application/vnd.api+json");
            request.AddHeader("Content-Type", "application/vnd.api+json");
            request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);
            var createData = new
            {

                data = new
                {
                    type = "checkouts",
                    attributes = new
                    {
                        custom_price = (int)(eurPrice * 100),
                        product_options = new
                        {
                            name = product.Title,
                            redirect_url = options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"],
                            receipt_button_text = "Go to your account",
                            description = product.Description ?? "Will be credited to your account",
                        },
                        checkout_data = new
                        {
                            email = options?.UserEmail,
                            custom = new
                            {
                                user_id = user.ExternalId.ToString(),
                                product_id = product.Id.ToString(),
                                coin_amount = ((int)coinAmount).ToString()
                            },
                        },
                        expires_at = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
                    },
                    relationships = new
                    {
                        store = new
                        {
                            data = new
                            {
                                type = "stores",
                                id = config["LEMONSQUEEZY:STORE_ID"]
                            }
                        },
                        variant = new
                        {
                            data = new
                            {
                                type = "variants",
                                id = config["LEMONSQUEEZY:VARIANT_ID"]
                            }
                        }
                    }
                }
            };
            var json = JsonConvert.SerializeObject(createData, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
            request.AddJsonBody(json);
            _logger.LogInformation($"Creating lemonsqueezy checkout with: \n{json}");
            var response = await restclient.ExecuteAsync(request);
            _logger.LogInformation(response.Content);
            var result = JsonConvert.DeserializeObject(response.Content);
            var data = JObject.Parse(result.ToString());
            var checkoutId = (string)data["data"]["id"];
            var link = (string)data["data"]["attributes"]["url"];
            return new TopUpIdResponse()
            {
                DirctLink = link,
                Id = checkoutId
            };
        }

        private async Task<TopUpProduct> GetTopupProduct(string productId, string provider)
        {
            var product = await productService.GetTopupProduct(productId);
            if (product.ProviderSlug != provider)
                throw new ApiException("Product is not purchaseable via this provider");
            return product;
        }

        private static void GetPriceAndCoins(TopUpOptions options, TopUpProduct product, out decimal eurPrice, out decimal coinAmount)
        {
            eurPrice = product.Price;
            coinAmount = product.Cost;
            if ((options?.TopUpAmount ?? 0) > 0)
            {
                var targetCoins = options.TopUpAmount;
                if (targetCoins < product.Cost)
                    throw new ApiException($"The topUpAmount has to be bigger than the cost of product {product.Slug} ({product.Cost.ToString("0,##")})");
                eurPrice = Math.Round(eurPrice * targetCoins / product.Cost, 2);
                coinAmount = targetCoins;
            }
        }

        /// <summary>
        /// Creates a custom topup that is instantly credited
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="topUp"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("custom")]
        public async Task<TopUpIdResponse> CreateCustom(string userId, CustomTopUp topUp)
        {
            var user = await userService.GetOrCreate(userId);
            var product = await productService.GetTopupProduct(topUp.ProductId);
            if (topUp.Amount > product.Cost)
                throw new ApiException($"The requested amount is larger than the maximum allowed of {product.Cost}");
            await transactionService.AddCustomTopUp(userId, topUp);
            return null;
        }


        /// <summary>
        /// Compensates users of a service for something
        /// </summary>
        /// <param name="details"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("compensate")]
        public async Task<(int, int)> Compensate(Compensation details)
        {
            var product = await productService.GetTopupProduct("compensation");
            var eurPrice = product.Price;
            if (details.When == default)
                details.When = DateTime.UtcNow;
            var users = await userService.GetUsersOwning(details.ProductId, details.When);
            var failedCount = 0;
            foreach (var item in users)
            {
                try
                {
                    await transactionService.CreateTransactionInTransaction(product, item, details.Amount, details.Reference);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "topup already existant for user");
                    failedCount++;
                }
            }
            return (users.Count(), failedCount);
        }
    }
}
