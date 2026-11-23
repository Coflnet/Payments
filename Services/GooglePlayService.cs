using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Coflnet.Payments.Models.GooglePay;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Service for verifying Google Play purchases
    /// </summary>
    public class GooglePlayService : IGooglePlayPublisher
    {
        private readonly ILogger<GooglePlayService> _logger;
        private readonly GooglePlaySettings _settings;
        private readonly AndroidPublisherService _androidPublisherService;

        public GooglePlayService(ILogger<GooglePlayService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _settings = configuration.GetSection("GOOGLEPAY").Get<GooglePlaySettings>()
                       ?? throw new ArgumentException("Google Pay settings not configured");
            try
            {
                _androidPublisherService = CreateAndroidPublisherService();
            }
            catch (Exception e)
            {
                _logger.LogError("Failed to initialize Google Play Service ({ErrorType})", e.GetType().Name);
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
                _logger.LogError("Failed to create Android Publisher Service ({ErrorType})", ex.GetType().Name);
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
                _logger.LogError("Failed to verify Google Play product purchase for product {ProductId} ({ErrorType})", productId, ex.GetType().Name);
                throw;
            }
        }

        public Task<SubscriptionPurchaseV2> GetSubscriptionV2(string token) =>
            _androidPublisherService.Purchases.Subscriptionsv2.Get(_settings.PackageName, token).ExecuteAsync();

        /// <inheritdoc />
        public async Task<(Money Total, Money Tax)> GetOrderAmounts(string orderId)
        {
            var order = await _androidPublisherService.Orders.Get(_settings.PackageName, orderId).ExecuteAsync();
            return (order.Total, order.Tax);
        }

        public async Task<bool> IsVoidedPurchase(string orderId, string purchaseToken)
        {
            var request = _androidPublisherService.Purchases.Voidedpurchases.List(_settings.PackageName);
            request.Type = 1;
            // Google's default startTime is 30 days before it receives the request.
            do
            {
                var page = await request.ExecuteAsync();
                if (page.VoidedPurchases?.Any(p => p.OrderId == orderId && p.PurchaseToken == purchaseToken) == true)
                    return true;
                request.Token = page.TokenPagination?.NextPageToken;
            } while (!string.IsNullOrEmpty(request.Token));
            return false;
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
                _logger.LogError("Failed to acknowledge Google Play product purchase for product {ProductId} ({ErrorType})", productId, ex.GetType().Name);
                throw;
            }
        }

        public async Task AcknowledgeSubscription(string productId, string token)
        {
            var body = new Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchasesAcknowledgeRequest();
            await _androidPublisherService.Purchases.Subscriptions.Acknowledge(body, _settings.PackageName, productId, token).ExecuteAsync();
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
        /// Disposes the Android Publisher Service
        /// </summary>
        public void Dispose()
        {
            _androidPublisherService?.Dispose();
        }
    }
}
