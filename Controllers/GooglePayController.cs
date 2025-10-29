using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
        private readonly GooglePlayService _googlePlayService;
        private readonly TransactionService _transactionService;
        private readonly IPaymentEventProducer _paymentEventProducer;
        private readonly ProductService _productService;

        /// <summary>
        /// Initializes a new instance of the GooglePayController
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="googlePlayService">Google Play service</param>
        /// <param name="transactionService">Transaction service</param>
        /// <param name="paymentEventProducer">Payment event producer</param>
        /// <param name="productService">Product service</param>
        public GooglePayController(
            ILogger<GooglePayController> logger,
            GooglePlayService googlePlayService,
            TransactionService transactionService,
            IPaymentEventProducer paymentEventProducer,
            ProductService productService)
        {
            _logger = logger;
            _googlePlayService = googlePlayService;
            _transactionService = transactionService;
            _paymentEventProducer = paymentEventProducer;
            _productService = productService;
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
                customAmount: 0,
                paymentMethod: "googlepay",
                isSubscription: false,
                logContext: $"product {request.ProductId}"
            );
            if (!response.Value.IsValid)
            {
                _logger.LogWarning("Google Play purchase verification failed for product {ProductId}, token {token} user {UserId}: {ErrorMessage}",
                    request.ProductId, request.PurchaseToken, request.UserId, response.Value.ErrorMessage);
            }
            return response;
        }

        /// <summary>
        /// Verifies and processes a Google Play subscription purchase
        /// </summary>
        /// <param name="request">The Google Play subscription verification request</param>
        /// <returns>Subscription verification response</returns>
        [HttpPost("verify-subscription")]
        public async Task<ActionResult<GooglePlayPurchaseResponse>> VerifySubscription([FromBody] GooglePlaySubscriptionRequest request)
        {
            return await ProcessGooglePlayTransaction(
                productId: request.SubscriptionId,
                packageName: request.PackageName,
                purchaseToken: request.PurchaseToken,
                userId: request.UserId,
                customAmount: 0,
                paymentMethod: "googlepay_subscription",
                isSubscription: true,
                logContext: $"subscription {request.SubscriptionId}"
            );
        }

        /// <summary>
        /// Common method to process Google Play transactions (both products and subscriptions)
        /// </summary>
        private async Task<ActionResult<GooglePlayPurchaseResponse>> ProcessGooglePlayTransaction(
            string productId,
            string packageName,
            string purchaseToken,
            string userId,
            decimal customAmount,
            string paymentMethod,
            bool isSubscription,
            string logContext)
        {
            try
            {
                _logger.LogInformation("Received Google Play {Type} verification request for {LogContext} by user {UserId}",
                    isSubscription ? "subscription" : "purchase", logContext, userId);

                if (!ModelState.IsValid)
                {
                    return CreateBadRequestResponse("Invalid request data");
                }

                // Verify with Google Play and get order ID
                string orderId;
                int? purchaseState = null;

                if (isSubscription)
                {
                    var subscription = await _googlePlayService.VerifySubscriptionPurchaseAsync(packageName, productId, purchaseToken);
                    
                    if (subscription.PaymentState != 1) // 1 = Payment received
                    {
                        _logger.LogWarning("Invalid subscription payment state for {LogContext}: {PaymentState}", logContext, subscription.PaymentState);
                        return CreateBadRequestResponse("Subscription payment is not valid");
                    }

                    if (subscription.AcknowledgementState == 1) // 1 = Acknowledged
                    {
                        _logger.LogWarning("Subscription already acknowledged for {LogContext}", logContext);
                        return CreateBadRequestResponse($"{(isSubscription ? "Subscription" : "Purchase")} already processed");
                    }

                    orderId = subscription.OrderId;
                }
                else
                {
                    var purchase = await _googlePlayService.VerifyProductPurchaseAsync(packageName, productId, purchaseToken);
                    
                    if (purchase.PurchaseState != 0) // 0 = Purchased
                    {
                        _logger.LogWarning("Invalid purchase state for {LogContext}: {PurchaseState}", logContext, purchase.PurchaseState);
                        return CreateBadRequestResponse("Purchase is not in valid state", purchase.PurchaseState);
                    }

                    if (purchase.AcknowledgementState == 1) // 1 = Acknowledged
                    {
                        _logger.LogWarning("Purchase already acknowledged for {LogContext}", logContext);
                        return CreateBadRequestResponse($"{(isSubscription ? "Subscription" : "Purchase")} already processed");
                    }

                    orderId = purchase.OrderId;
                    purchaseState = purchase.PurchaseState;
                }

                // Get product from database
                var product = await GetProductByProvider(productId, logContext);
                if (product == null)
                {
                    return CreateBadRequestResponse($"{(isSubscription ? "Subscription" : "Product")} not supported");
                }

                // Process transaction
                await _transactionService.AddTopUp(product.Id, userId, orderId, (long)customAmount);

                // Acknowledge with Google Play
                if (isSubscription)
                {
                    await _googlePlayService.AcknowledgeSubscriptionPurchaseAsync(packageName, productId, purchaseToken);
                }
                else
                {
                    await _googlePlayService.AcknowledgeProductPurchaseAsync(packageName, productId, purchaseToken);
                }

                // Send payment event
                await _paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = (double)product.Price,
                    ProductId = product.Id.ToString(),
                    UserId = userId,
                    Currency = product.CurrencyCode,
                    PaymentMethod = paymentMethod,
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = orderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play {Type} for {LogContext}, user {UserId}",
                    isSubscription ? "subscription" : "purchase", logContext, userId);

                var response = new GooglePlayPurchaseResponse { IsValid = true };
                if (purchaseState.HasValue)
                {
                    response.PurchaseState = purchaseState.Value;
                }
                return Ok(response);
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogWarning("Duplicate transaction for Google Play {LogContext}, user {UserId}", logContext, userId);
                
                // Still acknowledge the purchase/subscription if it was a duplicate
                await HandleDuplicateTransactionAcknowledgment(packageName, productId, purchaseToken, isSubscription);

                return Ok(new GooglePlayPurchaseResponse 
                { 
                    IsValid = true, 
                    ErrorMessage = $"{(isSubscription ? "Subscription" : "Transaction")} already processed" 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify Google Play {Type} for {LogContext}", 
                    isSubscription ? "subscription" : "purchase", logContext);
                
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

        /// <summary>
        /// Helper method to handle acknowledgment for duplicate transactions
        /// </summary>
        private async Task HandleDuplicateTransactionAcknowledgment(string packageName, string productId, string purchaseToken, bool isSubscription)
        {
            try
            {
                if (isSubscription)
                {
                    await _googlePlayService.AcknowledgeSubscriptionPurchaseAsync(packageName, productId, purchaseToken);
                }
                else
                {
                    await _googlePlayService.AcknowledgeProductPurchaseAsync(packageName, productId, purchaseToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge duplicate {Type}", isSubscription ? "subscription" : "purchase");
            }
        }
    }
}
