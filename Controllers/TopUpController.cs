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
        private readonly LemonSqueezyService lemonSqueezyService;
        private readonly CreatorCodeService creatorCodeService;

        /// <summary>
        /// Creates a new instance of the <see cref="TopUpController"/> class
        /// </summary>
        public TopUpController(ILogger<TopUpController> logger,
            PaymentContext context,
            Coflnet.Payments.Services.ProductService productService,
            Coflnet.Payments.Services.TransactionService transactionService,
            IConfiguration config,
            UserService userService,
            PayPalHttpClient paypalClient,
            LemonSqueezyService lemonSqueezyService,
            CreatorCodeService creatorCodeService)
        {
            _logger = logger;
            db = context;
            this.productService = productService;
            this.config = config;
            this.userService = userService;
            this.paypalClient = paypalClient;
            this.transactionService = transactionService;
            this.lemonSqueezyService = lemonSqueezyService;
            this.creatorCodeService = creatorCodeService;
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

            var (eurPrice, coinAmount, validatedCode) = await GetPriceAndCoins(topupotions, product);

            var instance = await AttemptBlockFraud(topupotions, user, product, eurPrice);
            if(instance.SessionId != null)
            {
                _logger.LogInformation("Stripe session already exists for user {userId} with id {sessionId}", user.Id, instance.SessionId);
                return new TopUpIdResponse { Id = instance.SessionId, DirctLink = "https://checkout.stripe.com/c/pay/" + instance.SessionId };
            }

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
            var country = topupotions.Locale?.Split('-')[0];
            if (!CallbackController.DoWeSellto(country, null))
                throw new ApiException($"We are sorry but we can not sell to your country ({country}) at this time");
        }

        private async Task<PaymentRequest> AttemptBlockFraud(TopUpOptions topupotions, User user, TopUpProduct product, decimal eurPrice)
        {
            PaymentRequest request = null;
            request = await transactionService.WithTransactionAsync<PaymentRequest>(async (transaction, owns) =>
            {
            if (!System.Net.IPAddress.TryParse(topupotions.UserIp, out var userIp))
                throw new ApiException("Invalid user IP");
            // check existing requests 
            var minTime = DateTime.UtcNow.AddHours(-10);
            var existingRequests = await db.PaymentRequests
                .Where(r => (r.User.Id == user.Id || r.CreateOnIp == userIp) && r.CreatedAt > minTime)
                .Where(r => r.State >= PaymentRequest.Status.CREATED && r.State < PaymentRequest.Status.PAID)
                .Include(r => r.User).Include(r => r.ProductId)
                .ToListAsync();
            if (existingRequests.Count(r => r.CreatedAt >= DateTime.UtcNow.AddMinutes(-10)) > 1)
                throw new ApiException("Too many payment requests from you, please try again later");
            if (existingRequests.Count > 5 || existingRequests.Count > 3 && existingRequests.Select(e => e.CreateOnIp).Distinct().Count() < 3)
                throw new ApiException($"Too many payment requests from you, please ask for support on discord or email support@coflnet.com with {topupotions?.Fingerprint?.Substring(0, 5)}");

            var match = existingRequests.FirstOrDefault(r => r.User.Id == user.Id && r.ProductId.Id == product.Id && r.State < PaymentRequest.Status.PAID && r.CreatedAt > DateTime.UtcNow.AddMinutes(-10) && r.SessionId != null);
            if (match != null)
                return match; // already existing request, return it

            request = new PaymentRequest()
            {
                User = user,
                ProductId = db.TopUpProducts.Find(product.Id),
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
                _logger.LogInformation("Created payment request {requestId} for user {userId}, existing {existingCount}", request.Id, user.Id, existingRequests.Count);
                return request;
            });
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
            if (user.Country == null && options.Locale != null)
            {
                user.Country = options.Locale.Split('-').Last();
                await db.SaveChangesAsync();
            }
            if (user.Ip == null)
            {
                user.Ip = options.UserIp;
                await db.SaveChangesAsync();
            }
            if (user.Country != null && !CallbackController.DoWeSellto(user.Country, null)) // maybe not available the first time but updated from the paypal webhook
                throw new ApiException($"We are sorry but we can not sell to your country ({user.Country}) at this time, please make sure to select the correct country in the selection and try again with the avilable payment provider");
            Console.WriteLine("Creating paypal payment for user {0} from {1}", user.Id, user.Country);
            var product = await GetTopupProduct(productId, "paypal");
            var (eurPrice, coinAmount, validatedCode) = await GetPriceAndCoins(options, product);
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
                        CustomId = product.Id.ToString() + ";" + coinAmount.ToString("0.##")  + ";" + user.ExternalId,
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
            var (eurPrice, coinAmount, validatedCode) = await GetPriceAndCoins(options, product);
            var variantId = config["LEMONSQUEEZY:VARIANT_ID"];
            return await lemonSqueezyService.SetupPayment(options, user, product, eurPrice, coinAmount, variantId, false);
        }
        [HttpPost]
        [Route("lemonsqueezy/subscribe")]
        public async Task<TopUpIdResponse> LemonSqueezySubscribe(string userId, string productId, [FromBody] TopUpOptions options = null)
        {
            var user = await userService.GetOrCreate(userId);
            var product = await productService.GetTopupProduct(productId);

            Console.WriteLine(JsonConvert.SerializeObject(product));
            if (!product.Type.HasFlag(Product.ProductType.SERVICE))
                throw new ApiException("Product is not a service, can't be subscribed to");
            var (eurPrice, coinAmount, validatedCode) = await GetPriceAndCoins(options, product);
            var variantId = config["LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID"];
            if (product.OwnershipSeconds == (int)TimeSpan.FromDays(365).TotalSeconds)
                variantId = config["LEMONSQUEEZY:YEAR_SUBSCRIPTION_VARIANT_ID"];
            return await lemonSqueezyService.SetupPayment(options, user, product, eurPrice, coinAmount, variantId, true);
        }

        private async Task<TopUpProduct> GetTopupProduct(string productId, string provider)
        {
            var product = await productService.GetTopupProduct(productId);
            if (product.ProviderSlug != provider)
                throw new ApiException("Product is not purchaseable via this provider");
            // readonly copy
            return new(product);
        }

        private async Task<(decimal eurPrice, decimal coinAmount, CreatorCode validatedCode)> GetPriceAndCoins(TopUpOptions options, TopUpProduct product)
        {
            decimal eurPrice = product.Price;
            decimal coinAmount = product.Cost;
            CreatorCode validatedCode = null;

            // Validate and apply creator code discount if provided
            if (!string.IsNullOrWhiteSpace(options?.CreatorCode))
            {
                validatedCode = await creatorCodeService.ValidateCreatorCodeAsync(options.CreatorCode);
                if (validatedCode == null)
                {
                    throw new ApiException("Invalid or expired creator code");
                }

                // Apply discount to the base price
                var discount = Math.Round(eurPrice * validatedCode.DiscountPercent / 100, 2);
                eurPrice -= discount;
                
                _logger.LogInformation("Applied creator code {CreatorCode} with {DiscountPercent}% discount to product {ProductSlug}. Original price: {OriginalPrice}, Discounted price: {DiscountedPrice}",
                    validatedCode.Code, validatedCode.DiscountPercent, product.Slug, product.Price, eurPrice);
            }

            // Apply custom coin amount if specified
            if ((options?.TopUpAmount ?? 0) > 0)
            {
                var targetCoins = options.TopUpAmount;
                if (targetCoins < product.Cost)
                    throw new ApiException($"The topUpAmount has to be bigger than the cost of product {product.Slug} ({product.Cost.ToString("0,##")})");
                
                // Scale the price based on the coin amount (after discount has been applied)
                eurPrice = Math.Round(eurPrice * targetCoins / product.Cost, 2);
                coinAmount = targetCoins;
                
                // format with dot as thousands seperator
                product.Title = product.Title.Replace(".", ",").Replace(product.Cost.ToString("0,##"), targetCoins.ToString("0,##"));
            }

            return (eurPrice, coinAmount, validatedCode);
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
