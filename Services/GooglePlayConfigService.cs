using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Service for managing Google Play product mappings and configuration
    /// </summary>
    public class GooglePlayConfigService
    {
        private readonly Dictionary<string, GooglePlayProductMapping> _productMappings;
        private readonly Dictionary<string, GooglePlayProductMapping> _subscriptionMappings;

        /// <summary>
        /// Initializes a new instance of the GooglePlayConfigService
        /// </summary>
        /// <param name="configuration">Configuration instance</param>
        public GooglePlayConfigService(IConfiguration configuration)
        {
            _productMappings = LoadProductMappings(configuration, "GOOGLEPAY:PRODUCTS");
            _subscriptionMappings = LoadProductMappings(configuration, "GOOGLEPAY:SUBSCRIPTIONS");
        }

        /// <summary>
        /// Gets the internal product ID for a Google Play product
        /// </summary>
        /// <param name="googlePlayProductId">The Google Play product ID</param>
        /// <returns>Internal product ID, or 0 if not found</returns>
        public int GetInternalProductId(string googlePlayProductId)
        {
            return _productMappings.TryGetValue(googlePlayProductId, out var mapping) ? mapping.InternalProductId : 0;
        }

        /// <summary>
        /// Gets the internal product ID for a Google Play subscription
        /// </summary>
        /// <param name="googlePlaySubscriptionId">The Google Play subscription ID</param>
        /// <returns>Internal product ID, or 0 if not found</returns>
        public int GetInternalSubscriptionProductId(string googlePlaySubscriptionId)
        {
            return _subscriptionMappings.TryGetValue(googlePlaySubscriptionId, out var mapping) ? mapping.InternalProductId : 0;
        }

        /// <summary>
        /// Gets the price for a Google Play product
        /// </summary>
        /// <param name="googlePlayProductId">The Google Play product ID</param>
        /// <returns>Product price, or 0.0 if not found</returns>
        public double GetProductPrice(string googlePlayProductId)
        {
            return _productMappings.TryGetValue(googlePlayProductId, out var mapping) ? mapping.Price : 0.0;
        }

        /// <summary>
        /// Gets the price for a Google Play subscription
        /// </summary>
        /// <param name="googlePlaySubscriptionId">The Google Play subscription ID</param>
        /// <returns>Subscription price, or 0.0 if not found</returns>
        public double GetSubscriptionPrice(string googlePlaySubscriptionId)
        {
            return _subscriptionMappings.TryGetValue(googlePlaySubscriptionId, out var mapping) ? mapping.Price : 0.0;
        }

        /// <summary>
        /// Gets the currency for a Google Play product
        /// </summary>
        /// <param name="googlePlayProductId">The Google Play product ID</param>
        /// <returns>Currency code, or "USD" if not found</returns>
        public string GetProductCurrency(string googlePlayProductId)
        {
            return _productMappings.TryGetValue(googlePlayProductId, out var mapping) ? mapping.Currency : "USD";
        }

        /// <summary>
        /// Gets the currency for a Google Play subscription
        /// </summary>
        /// <param name="googlePlaySubscriptionId">The Google Play subscription ID</param>
        /// <returns>Currency code, or "USD" if not found</returns>
        public string GetSubscriptionCurrency(string googlePlaySubscriptionId)
        {
            return _subscriptionMappings.TryGetValue(googlePlaySubscriptionId, out var mapping) ? mapping.Currency : "USD";
        }

        /// <summary>
        /// Gets the price for an internal product ID (reverse lookup)
        /// </summary>
        /// <param name="internalProductId">The internal product ID</param>
        /// <returns>Product price, or 0.0 if not found</returns>
        public double GetPriceByInternalProductId(int internalProductId)
        {
            var mapping = _productMappings.Values.FirstOrDefault(m => m.InternalProductId == internalProductId) ??
                         _subscriptionMappings.Values.FirstOrDefault(m => m.InternalProductId == internalProductId);
            return mapping?.Price ?? 0.0;
        }

        /// <summary>
        /// Gets the currency for an internal product ID (reverse lookup)
        /// </summary>
        /// <param name="internalProductId">The internal product ID</param>
        /// <returns>Currency code, or "USD" if not found</returns>
        public string GetCurrencyByInternalProductId(int internalProductId)
        {
            var mapping = _productMappings.Values.FirstOrDefault(m => m.InternalProductId == internalProductId) ??
                         _subscriptionMappings.Values.FirstOrDefault(m => m.InternalProductId == internalProductId);
            return mapping?.Currency ?? "USD";
        }

        /// <summary>
        /// Loads product mappings from configuration
        /// </summary>
        /// <param name="configuration">Configuration instance</param>
        /// <param name="sectionName">Configuration section name</param>
        /// <returns>Dictionary of product mappings</returns>
        private Dictionary<string, GooglePlayProductMapping> LoadProductMappings(IConfiguration configuration, string sectionName)
        {
            var mappings = new Dictionary<string, GooglePlayProductMapping>();
            var section = configuration.GetSection(sectionName);

            foreach (var child in section.GetChildren())
            {
                var mapping = new GooglePlayProductMapping
                {
                    GooglePlayProductId = child.Key,
                    InternalProductId = child.GetValue<int>("InternalProductId"),
                    Price = child.GetValue<double>("Price"),
                    Currency = child.GetValue<string>("Currency") ?? "USD"
                };

                mappings[child.Key] = mapping;
            }

            // Add default mappings if no configuration is found
            if (!mappings.Any())
            {
                AddDefaultMappings(mappings, sectionName.Contains("SUBSCRIPTIONS"));
            }

            return mappings;
        }

        /// <summary>
        /// Adds default product mappings when no configuration is found
        /// </summary>
        /// <param name="mappings">Mappings dictionary to populate</param>
        /// <param name="isSubscription">Whether these are subscription mappings</param>
        private void AddDefaultMappings(Dictionary<string, GooglePlayProductMapping> mappings, bool isSubscription)
        {
            if (isSubscription)
            {
                // Default subscription mappings
                mappings["premium_monthly"] = new GooglePlayProductMapping { GooglePlayProductId = "premium_monthly", InternalProductId = 7, Price = 9.99, Currency = "USD" };
                mappings["premium_yearly"] = new GooglePlayProductMapping { GooglePlayProductId = "premium_yearly", InternalProductId = 8, Price = 99.99, Currency = "USD" };
                mappings["vip_monthly"] = new GooglePlayProductMapping { GooglePlayProductId = "vip_monthly", InternalProductId = 9, Price = 19.99, Currency = "USD" };
                mappings["vip_yearly"] = new GooglePlayProductMapping { GooglePlayProductId = "vip_yearly", InternalProductId = 10, Price = 199.99, Currency = "USD" };
            }
            else
            {
                // Default product mappings
                mappings["coins_100"] = new GooglePlayProductMapping { GooglePlayProductId = "coins_100", InternalProductId = 1, Price = 0.99, Currency = "USD" };
                mappings["coins_500"] = new GooglePlayProductMapping { GooglePlayProductId = "coins_500", InternalProductId = 2, Price = 4.99, Currency = "USD" };
                mappings["coins_1000"] = new GooglePlayProductMapping { GooglePlayProductId = "coins_1000", InternalProductId = 3, Price = 9.99, Currency = "USD" };
                mappings["coins_2500"] = new GooglePlayProductMapping { GooglePlayProductId = "coins_2500", InternalProductId = 4, Price = 19.99, Currency = "USD" };
                mappings["coins_5000"] = new GooglePlayProductMapping { GooglePlayProductId = "coins_5000", InternalProductId = 5, Price = 39.99, Currency = "USD" };
                mappings["premium_boost"] = new GooglePlayProductMapping { GooglePlayProductId = "premium_boost", InternalProductId = 6, Price = 2.99, Currency = "USD" };
            }
        }
    }

    /// <summary>
    /// Mapping between Google Play product and internal product
    /// </summary>
    public class GooglePlayProductMapping
    {
        /// <summary>
        /// The Google Play product ID
        /// </summary>
        public string GooglePlayProductId { get; set; }

        /// <summary>
        /// The internal product ID
        /// </summary>
        public int InternalProductId { get; set; }

        /// <summary>
        /// The product price
        /// </summary>
        public double Price { get; set; }

        /// <summary>
        /// The product currency
        /// </summary>
        public string Currency { get; set; }
    }
}
