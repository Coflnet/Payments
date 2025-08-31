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
        private readonly GooglePlayConfigService _configService;

        /// <summary>
        /// Initializes a new instance of the GooglePayController
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="googlePlayService">Google Play service</param>
        /// <param name="transactionService">Transaction service</param>
        /// <param name="paymentEventProducer">Payment event producer</param>
        /// <param name="configService">Google Play configuration service</param>
        public GooglePayController(
            ILogger<GooglePayController> logger,
            GooglePlayService googlePlayService,
            TransactionService transactionService,
            IPaymentEventProducer paymentEventProducer,
            GooglePlayConfigService configService)
        {
            _logger = logger;
            _googlePlayService = googlePlayService;
            _transactionService = transactionService;
            _paymentEventProducer = paymentEventProducer;
            _configService = configService;
        }

        /// <summary>
        /// Verifies and processes a Google Play product purchase
        /// </summary>
        /// <param name="request">The Google Play purchase verification request</param>
        /// <returns>Purchase verification response</returns>
        [HttpPost("verify")]
        public async Task<ActionResult<GooglePlayPurchaseResponse>> VerifyPurchase([FromBody] GooglePlayPurchaseRequest request)
        {
            try
            {
                _logger.LogInformation("Received Google Play purchase verification request for product {ProductId} by user {UserId}", 
                    request.ProductId, request.UserId);

                if (!ModelState.IsValid)
                {
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Invalid request data" 
                    });
                }

                // Verify the purchase with Google Play
                var purchase = await _googlePlayService.VerifyProductPurchaseAsync(
                    request.PackageName, 
                    request.ProductId, 
                    request.PurchaseToken);

                // Check if purchase is valid and not consumed
                if (purchase.PurchaseState != 0) // 0 = Purchased
                {
                    _logger.LogWarning("Invalid purchase state for product {ProductId}: {PurchaseState}", 
                        request.ProductId, purchase.PurchaseState);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Purchase is not in valid state",
                        PurchaseState = purchase.PurchaseState
                    });
                }

                // Check if purchase is already acknowledged/consumed
                if (purchase.AcknowledgementState == 1) // 1 = Acknowledged
                {
                    _logger.LogWarning("Purchase already acknowledged for product {ProductId}", request.ProductId);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Purchase already processed" 
                    });
                }

                // Map Google Play product to internal product
                var internalProductId = request.InternalProductId ?? _configService.GetInternalProductId(request.ProductId);
                if (internalProductId == 0)
                {
                    _logger.LogError("No internal product mapping found for Google Play product {ProductId}", request.ProductId);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Product not supported" 
                    });
                }

                // Process the transaction
                var customAmount = request.CustomAmount ?? 0;
                await _transactionService.AddTopUp(internalProductId, request.UserId, purchase.OrderId, customAmount);

                // Acknowledge the purchase with Google Play
                await _googlePlayService.AcknowledgeProductPurchaseAsync(
                    request.PackageName, 
                    request.ProductId, 
                    request.PurchaseToken);

                // Send payment event
                await _paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = _configService.GetProductPrice(request.ProductId),
                    ProductId = internalProductId.ToString(),
                    UserId = request.UserId,
                    Currency = _configService.GetProductCurrency(request.ProductId),
                    PaymentMethod = "googlepay",
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = purchase.OrderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play purchase for product {ProductId}, user {UserId}", 
                    request.ProductId, request.UserId);

                return Ok(new GooglePlayPurchaseResponse 
                { 
                    IsValid = true,
                    PurchaseState = purchase.PurchaseState
                });
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogWarning("Duplicate transaction for Google Play product {ProductId}, user {UserId}", 
                    request.ProductId, request.UserId);
                
                // Still acknowledge the purchase if it was a duplicate
                try
                {
                    await _googlePlayService.AcknowledgeProductPurchaseAsync(
                        request.PackageName, 
                        request.ProductId, 
                        request.PurchaseToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to acknowledge duplicate purchase");
                }

                return Ok(new GooglePlayPurchaseResponse 
                { 
                    IsValid = true, 
                    ErrorMessage = "Transaction already processed" 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify Google Play purchase for product {ProductId}", request.ProductId);
                
                return StatusCode(500, new GooglePlayPurchaseResponse 
                { 
                    IsValid = false, 
                    ErrorMessage = "Internal server error" 
                });
            }
        }

        /// <summary>
        /// Verifies and processes a Google Play subscription purchase
        /// </summary>
        /// <param name="request">The Google Play subscription verification request</param>
        /// <returns>Subscription verification response</returns>
        [HttpPost("verify-subscription")]
        public async Task<ActionResult<GooglePlayPurchaseResponse>> VerifySubscription([FromBody] GooglePlaySubscriptionRequest request)
        {
            try
            {
                _logger.LogInformation("Received Google Play subscription verification request for subscription {SubscriptionId} by user {UserId}", 
                    request.SubscriptionId, request.UserId);

                if (!ModelState.IsValid)
                {
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Invalid request data" 
                    });
                }

                // Verify the subscription with Google Play
                var subscription = await _googlePlayService.VerifySubscriptionPurchaseAsync(
                    request.PackageName, 
                    request.SubscriptionId, 
                    request.PurchaseToken);

                // Check if subscription is active
                if (subscription.PaymentState != 1) // 1 = Payment received
                {
                    _logger.LogWarning("Invalid subscription payment state for subscription {SubscriptionId}: {PaymentState}", 
                        request.SubscriptionId, subscription.PaymentState);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Subscription payment is not valid"
                    });
                }

                // Check if subscription is already acknowledged
                if (subscription.AcknowledgementState == 1) // 1 = Acknowledged
                {
                    _logger.LogWarning("Subscription already acknowledged for subscription {SubscriptionId}", request.SubscriptionId);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Subscription already processed" 
                    });
                }

                // Map Google Play subscription to internal product
                var internalProductId = _configService.GetInternalSubscriptionProductId(request.SubscriptionId);
                if (internalProductId == 0)
                {
                    _logger.LogError("No internal product mapping found for Google Play subscription {SubscriptionId}", request.SubscriptionId);
                    
                    return BadRequest(new GooglePlayPurchaseResponse 
                    { 
                        IsValid = false, 
                        ErrorMessage = "Subscription not supported" 
                    });
                }

                // Process the subscription transaction
                await _transactionService.AddTopUp(internalProductId, request.UserId, subscription.OrderId, 0);

                // Acknowledge the subscription with Google Play
                await _googlePlayService.AcknowledgeSubscriptionPurchaseAsync(
                    request.PackageName, 
                    request.SubscriptionId, 
                    request.PurchaseToken);

                // Send payment event
                await _paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = _configService.GetSubscriptionPrice(request.SubscriptionId),
                    ProductId = internalProductId.ToString(),
                    UserId = request.UserId,
                    Currency = _configService.GetSubscriptionCurrency(request.SubscriptionId),
                    PaymentMethod = "googlepay_subscription",
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = subscription.OrderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play subscription for subscription {SubscriptionId}, user {UserId}", 
                    request.SubscriptionId, request.UserId);

                return Ok(new GooglePlayPurchaseResponse 
                { 
                    IsValid = true
                });
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogWarning("Duplicate subscription transaction for Google Play subscription {SubscriptionId}, user {UserId}", 
                    request.SubscriptionId, request.UserId);
                
                return Ok(new GooglePlayPurchaseResponse 
                { 
                    IsValid = true, 
                    ErrorMessage = "Subscription already processed" 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify Google Play subscription for subscription {SubscriptionId}", request.SubscriptionId);
                
                return StatusCode(500, new GooglePlayPurchaseResponse 
                { 
                    IsValid = false, 
                    ErrorMessage = "Internal server error" 
                });
            }
        }
    }
}
