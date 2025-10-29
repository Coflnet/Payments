using System;
using System.IO;
using System.Threading.Tasks;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Models;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Service for verifying Google Play purchases
    /// </summary>
    public class GooglePlayService
    {
        private readonly ILogger<GooglePlayService> _logger;
        private readonly GooglePlaySettings _settings;
        private readonly AndroidPublisherService _androidPublisherService;

        public GooglePlayService(ILogger<GooglePlayService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _settings = configuration.GetSection("GOOGLEPAY").Get<GooglePlaySettings>()
                       ?? throw new ArgumentException("Google Pay settings not configured");
            logger.LogInformation(JsonSerializer.Serialize(_settings));
            try
            {
                _androidPublisherService = CreateAndroidPublisherService();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to initialize Google Play Service");
            }
        }

        /// <summary>
        /// Creates the Android Publisher Service for Google Play API
        /// </summary>
        /// <returns>Configured AndroidPublisherService</returns>
        private AndroidPublisherService CreateAndroidPublisherService()
        {
            try
            {
                GoogleCredential credential;

                if (File.Exists(_settings.ServiceAccountKeyFile))
                {
                    // Load from service account key file
                    using var stream = new FileStream(_settings.ServiceAccountKeyFile, FileMode.Open, FileAccess.Read);
                    credential = GoogleCredential.FromStream(stream)
                        .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
                }
                else
                {
                    // Try to load from JSON string in configuration
                    var keyContent = _settings.ServiceAccountKeyFile;
                    if (!string.IsNullOrEmpty(keyContent))
                    {
                        credential = GoogleCredential.FromJson(keyContent)
                            .CreateScoped(AndroidPublisherService.Scope.Androidpublisher);
                    }
                    else
                    {
                        throw new ArgumentException("Google Play service account key not found");
                    }
                }

                return new AndroidPublisherService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credential,
                    ApplicationName = _settings.ApplicationName
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Android Publisher Service");
                throw;
            }
        }

        /// <summary>
        /// Verifies a one-time product purchase from Google Play
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="productId">The product ID</param>
        /// <param name="purchaseToken">The purchase token</param>
        /// <returns>Purchase verification result</returns>
        public async Task<Google.Apis.AndroidPublisher.v3.Data.ProductPurchase> VerifyProductPurchaseAsync(
            string packageName, string productId, string purchaseToken)
        {
            try
            {
                _logger.LogInformation("Verifying Google Play product purchase for product {ProductId}", productId);

                var request = _androidPublisherService.Purchases.Products.Get(packageName, productId, purchaseToken);
                var purchase = await request.ExecuteAsync();

                _logger.LogInformation("Purchase verification completed for product {ProductId}, state: {PurchaseState}",
                    productId, purchase.PurchaseState);

                return purchase;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify Google Play product purchase for product {ProductId}", productId);
                throw;
            }
        }

        /// <summary>
        /// Verifies a subscription purchase from Google Play
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="subscriptionId">The subscription ID</param>
        /// <param name="purchaseToken">The purchase token</param>
        /// <returns>Subscription verification result</returns>
        public async Task<Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchase> VerifySubscriptionPurchaseAsync(
            string packageName, string subscriptionId, string purchaseToken)
        {
            try
            {
                _logger.LogInformation("Verifying Google Play subscription purchase for subscription {SubscriptionId}", subscriptionId);

                var request = _androidPublisherService.Purchases.Subscriptions.Get(packageName, subscriptionId, purchaseToken);
                var subscription = await request.ExecuteAsync();

                _logger.LogInformation("Subscription verification completed for subscription {SubscriptionId}, state: {PaymentState}",
                    subscriptionId, subscription.PaymentState);

                return subscription;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify Google Play subscription purchase for subscription {SubscriptionId}", subscriptionId);
                throw;
            }
        }

        /// <summary>
        /// Acknowledges a product purchase
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="productId">The product ID</param>
        /// <param name="purchaseToken">The purchase token</param>
        /// <returns>Task representing the acknowledgment operation</returns>
        public async Task AcknowledgeProductPurchaseAsync(string packageName, string productId, string purchaseToken)
        {
            try
            {
                _logger.LogInformation("Acknowledging Google Play product purchase for product {ProductId}", productId);

                var acknowledgeRequest = new Google.Apis.AndroidPublisher.v3.Data.ProductPurchasesAcknowledgeRequest();
                var request = _androidPublisherService.Purchases.Products.Acknowledge(acknowledgeRequest, packageName, productId, purchaseToken);
                await request.ExecuteAsync();

                _logger.LogInformation("Product purchase acknowledged for product {ProductId}", productId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge Google Play product purchase for product {ProductId}", productId);
                throw;
            }
        }

        /// <summary>
        /// Acknowledges a subscription purchase
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="subscriptionId">The subscription ID</param>
        /// <param name="purchaseToken">The purchase token</param>
        /// <returns>Task representing the acknowledgment operation</returns>
        public async Task AcknowledgeSubscriptionPurchaseAsync(string packageName, string subscriptionId, string purchaseToken)
        {
            try
            {
                _logger.LogInformation("Acknowledging Google Play subscription purchase for subscription {SubscriptionId}", subscriptionId);

                var acknowledgeRequest = new Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchasesAcknowledgeRequest();
                var request = _androidPublisherService.Purchases.Subscriptions.Acknowledge(acknowledgeRequest, packageName, subscriptionId, purchaseToken);
                await request.ExecuteAsync();

                _logger.LogInformation("Subscription purchase acknowledged for subscription {SubscriptionId}", subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acknowledge Google Play subscription purchase for subscription {SubscriptionId}", subscriptionId);
                throw;
            }
        }

        /// <summary>
        /// Gets in-app product details including localized pricing
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="productId">The product ID (SKU)</param>
        /// <returns>Product details including price information</returns>
        public async Task<Google.Apis.AndroidPublisher.v3.Data.InAppProduct> GetProductDetailsAsync(
            string packageName, string productId)
        {
            try
            {
                if (_androidPublisherService == null)
                {
                    throw new InvalidOperationException("Google Play service is not properly initialized. Check your Google Play service account credentials.");
                }

                _logger.LogInformation("Getting Google Play product details for product {ProductId}", productId);

                var request = _androidPublisherService.Inappproducts.Get(packageName, productId);
                var product = await request.ExecuteAsync();

                _logger.LogInformation("Product details retrieved for product {ProductId}", productId);

                return product;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Google Play product details for product {ProductId}", productId);
                throw;
            }
        }

        /// <summary>
        /// Gets subscription details including localized pricing
        /// </summary>
        /// <param name="packageName">The package name of the app</param>
        /// <param name="productId">The subscription ID (SKU)</param>
        /// <returns>Subscription details including price information</returns>
        public async Task<Google.Apis.AndroidPublisher.v3.Data.Subscription> GetSubscriptionDetailsAsync(
            string packageName, string productId)
        {
            try
            {
                if (_androidPublisherService == null)
                {
                    throw new InvalidOperationException("Google Play service is not properly initialized. Check your Google Play service account credentials.");
                }

                _logger.LogInformation("Getting Google Play subscription details for subscription {ProductId}", productId);

                var request = _androidPublisherService.Monetization.Subscriptions.Get(packageName, productId);
                var subscription = await request.ExecuteAsync();

                _logger.LogInformation("Subscription details retrieved for subscription {ProductId}", productId);

                return subscription;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Google Play subscription details for subscription {ProductId}", productId);
                throw;
            }
        }

        /// <summary>
        /// Disposes the Android Publisher Service
        /// </summary>
        public void Dispose()
        {
            _androidPublisherService?.Dispose();
        }
    }
}
