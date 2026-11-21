using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Payments.Controllers
{
    /// <summary>
    /// Controller for Google Pay purchase verification and management
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class GooglePayController : ControllerBase
    {
        private readonly ILogger<GooglePayController> _logger;
        private readonly IGooglePlayPublisher _googlePlayService;
        private readonly TransactionService _transactionService;
        private readonly IPaymentEventProducer _paymentEventProducer;
        private readonly ProductService _productService;
        private readonly PaymentContext _db;

        /// <summary>
        /// Initializes a new instance of the GooglePayController
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="googlePlayService">Google Play service</param>
        /// <param name="transactionService">Transaction service</param>
        /// <param name="paymentEventProducer">Payment event producer</param>
        /// <param name="productService">Product service</param>
        /// <param name="context">Ledger used to confirm acknowledged purchases were already credited</param>
        public GooglePayController(
            ILogger<GooglePayController> logger,
            IGooglePlayPublisher googlePlayService,
            TransactionService transactionService,
            IPaymentEventProducer paymentEventProducer,
            ProductService productService,
            PaymentContext context)
        {
            _logger = logger;
            _googlePlayService = googlePlayService;
            _transactionService = transactionService;
            _paymentEventProducer = paymentEventProducer;
            _productService = productService;
            _db = context;
        }

        /// <summary>
        /// Verifies and processes a Google Play product purchase
        /// </summary>
        /// <param name="request">The Google Play purchase verification request</param>
        /// <returns>Purchase verification response</returns>
        [HttpPost("verify")]
        public async Task<ActionResult<GooglePlayPurchaseResponse>> VerifyPurchase([FromBody] GooglePlayPurchaseRequest request)
        {
            var response = await ProcessGooglePlayTransaction(
                productId: request.ProductId,
                packageName: request.PackageName,
                purchaseToken: request.PurchaseToken,
                userId: request.UserId,
                logContext: $"product {request.ProductId}"
            );
            // The ProcessGooglePlayTransaction method may return an IActionResult (error) instead of a typed Value.
            // Protect against null Value to avoid NullReferenceException in the controller.
            if (response.Value == null ||!response.Value.IsValid)
            {
                _logger.LogWarning("Google Play purchase verification returned no typed value for product {ProductId}. Result object: {Result}",
                    request.ProductId, JsonConvert.SerializeObject(response));
                return response;
            }

            return response;
        }

        /// <summary>
        /// Gets localized pricing information for a Google Play product
        /// </summary>
        /// <param name="request">The product pricing request</param>
        /// <returns>Localized pricing information</returns>
        [HttpPost("product-pricing")]
        public async Task<ActionResult<GooglePlayProductPricingResponse>> GetProductPricing([FromBody] GooglePlayProductPricingRequest request)
        {
            try
            {
                _logger.LogInformation("Getting pricing for Google Play product {ProductId}", request.ProductId);

                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                // Get product details from Google Play
                var productDetails = await _googlePlayService.GetProductDetailsAsync(request.PackageName, request.ProductId);

                // Map to our response model
                var response = new GooglePlayProductPricingResponse
                {
                    ProductId = productDetails.Sku,
                    Status = productDetails.Status,
                    PurchaseType = productDetails.PurchaseType,
                    DefaultLanguage = productDetails.DefaultLanguage,
                    Listings = new Dictionary<string, LocalizedListing>(),
                    Prices = new Dictionary<string, PriceInfo>()
                };

                // Map localized listings
                if (productDetails.Listings != null)
                {
                    foreach (var listing in productDetails.Listings)
                    {
                        response.Listings[listing.Key] = new LocalizedListing
                        {
                            Title = listing.Value.Title,
                            Description = listing.Value.Description
                        };
                    }
                }

                // Map prices
                if (productDetails.Prices != null)
                {
                    foreach (var priceEntry in productDetails.Prices)
                    {
                        try
                        {
                            // Serialize the returned object to JSON and parse known fields without using reflection/dynamic
                            var valueObj = (object)priceEntry.Value;
                            var json = Newtonsoft.Json.JsonConvert.SerializeObject(valueObj ?? new object());
                            var j = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}");

                            // Try common field names used by Google API
                            var priceMicros = (string)(j["priceMicros"] ?? j["priceAmountMicros"] ?? j["price_amount_micros"] ?? j["price_micro"]);
                            var currency = (string)(j["currency"] ?? j["currencyCode"] ?? j["currency_code"]);
                            var formatted = (string)(j["formattedPrice"] ?? j["price"] ?? j["formatted_price"]);

                            // Fallback: if formatted price is missing, try to derive it from micros and currency
                            if (string.IsNullOrEmpty(formatted) && long.TryParse(priceMicros, out var micros) && !string.IsNullOrEmpty(currency))
                            {
                                try
                                {
                                    var amount = micros / 1_000_000m;
                                    formatted = string.Format("{0} {1:N2}", currency, amount);
                                }
                                catch
                                {
                                    // ignore formatting errors
                                }
                            }

                            response.Prices[priceEntry.Key] = new PriceInfo
                            {
                                PriceMicros = priceMicros,
                                Currency = currency,
                                FormattedPrice = formatted
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to map price entry for locale {Locale}", priceEntry.Key);
                            response.Prices[priceEntry.Key] = new PriceInfo
                            {
                                PriceMicros = null,
                                Currency = null,
                                FormattedPrice = null
                            };
                        }
                    }
                }

                _logger.LogInformation("Successfully retrieved pricing for Google Play product {ProductId}", request.ProductId);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pricing for Google Play product {ProductId}", request.ProductId);
                return StatusCode(500, new { error = "Failed to retrieve product pricing" });
            }
        }

        /// <summary>
        /// Processes Google Play product transactions
        /// </summary>
        private async Task<ActionResult<GooglePlayPurchaseResponse>> ProcessGooglePlayTransaction(
            string productId,
            string packageName,
            string purchaseToken,
            string userId,
            string logContext)
        {
            try
            {
                _logger.LogInformation("Received Google Play {Type} verification request for {LogContext} by user {UserId}",
                    "purchase", logContext, userId);

                if (!ModelState.IsValid)
                {
                    return CreateBadRequestResponse("Invalid request data");
                }

                var purchase = await _googlePlayService.VerifyProductPurchaseAsync(packageName, productId, purchaseToken);
                if (purchase.PurchaseState != 0) // 0 = Purchased
                {
                    _logger.LogWarning("Invalid purchase state for {LogContext}: {PurchaseState}", logContext, purchase.PurchaseState);
                    return CreateBadRequestResponse("Purchase is not in valid state", purchase.PurchaseState);
                }
                var orderId = purchase.OrderId;
                var purchaseState = purchase.PurchaseState;

                // Get product from database
                var product = await GetProductByProvider(productId, logContext);
                if (product == null)
                {
                    return CreateBadRequestResponse("Product not supported");
                }

                if (purchase.AcknowledgementState == 1)
                {
                    var credited = await _db.FiniteTransactions.AnyAsync(t =>
                        t.ProductId == product.Id && t.User.ExternalId == userId && t.Reference == orderId && t.Amount > 0);
                    return credited
                        ? Ok(new GooglePlayPurchaseResponse { IsValid = true, PurchaseState = purchaseState })
                        : CreateBadRequestResponse("Purchase already acknowledged without a matching credit", purchaseState);
                }

                try
                {
                    await _transactionService.AddTopUp(product.Id, userId, orderId);
                }
                catch (TransactionService.DupplicateTransactionException)
                {
                    await _googlePlayService.AcknowledgeProductPurchaseAsync(packageName, productId, purchaseToken);
                    return Ok(new GooglePlayPurchaseResponse { IsValid = true, PurchaseState = purchaseState });
                }

                await _googlePlayService.AcknowledgeProductPurchaseAsync(packageName, productId, purchaseToken);

                // Send payment event
                await _paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = (double)product.Price,
                    ProductId = product.Id.ToString(),
                    ProductTitle = product.Title,
                    UserId = userId,
                    Currency = product.CurrencyCode,
                    PaymentMethod = "googlepay",
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = orderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play {Type} for {LogContext}, user {UserId}",
                    "purchase", logContext, userId);

                var response = new GooglePlayPurchaseResponse { IsValid = true };
                if (purchaseState.HasValue)
                {
                    response.PurchaseState = purchaseState.Value;
                }
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to verify Google Play purchase for {LogContext} ({ErrorType})",
                    logContext, ex.GetType().Name);

                return StatusCode(500, new GooglePlayPurchaseResponse
                {
                    IsValid = false,
                    ErrorMessage = "Internal server error"
                });
            }
        }

        /// <summary>
        /// Helper method to get product from database by provider
        /// </summary>
        private async Task<TopUpProduct> GetProductByProvider(string productId, string logContext)
        {
            try
            {
                return await _productService.GetTopupProductByProvider(productId, "googlepay");
            }
            catch (ApiException)
            {
                _logger.LogError("No internal product found for Google Play {LogContext}", logContext);
                return null;
            }
        }

        /// <summary>
        /// Helper method to create bad request responses
        /// </summary>
        private BadRequestObjectResult CreateBadRequestResponse(string errorMessage, int? purchaseState = null)
        {
            var response = new GooglePlayPurchaseResponse
            {
                IsValid = false,
                ErrorMessage = errorMessage
            };

            if (purchaseState.HasValue)
            {
                response.PurchaseState = purchaseState.Value;
            }

            return BadRequest(response);
        }

    }
}
