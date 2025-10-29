using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace Coflnet.Payments.Models.GooglePay
{
    /// <summary>
    /// Google Play purchase verification request
    /// </summary>
    public class GooglePlayPurchaseRequest
    {
        /// <summary>
        /// The product ID from the Google Play Console
        /// </summary>
        [Required]
        public string ProductId { get; set; }

        /// <summary>
        /// The purchase token from Google Play
        /// </summary>
        [Required]
        public string PurchaseToken { get; set; }

        /// <summary>
        /// The package name of the application
        /// </summary>
        [Required]
        public string PackageName { get; set; }

        /// <summary>
        /// The user ID who made the purchase
        /// </summary>
        [Required]
        public string UserId { get; set; }

        /// <summary>
        /// Custom amount for the purchase (optional)
        /// </summary>
        public long? CustomAmount { get; set; }
    }

    /// <summary>
    /// Google Play purchase verification response
    /// </summary>
    public class GooglePlayPurchaseResponse
    {
        /// <summary>
        /// Whether the purchase was successfully verified
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Error message if verification failed
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// The transaction ID created in our system
        /// </summary>
        public long? TransactionId { get; set; }

        /// <summary>
        /// The purchase state from Google Play
        /// </summary>
        public int? PurchaseState { get; set; }

        /// <summary>
        /// The amount of the purchase in micro-units
        /// </summary>
        public long? PriceAmountMicros { get; set; }

        /// <summary>
        /// The currency code for the purchase
        /// </summary>
        public string PriceCurrencyCode { get; set; }
    }

    /// <summary>
    /// Google Play subscription verification request
    /// </summary>
    public class GooglePlaySubscriptionRequest
    {
        /// <summary>
        /// The subscription ID from the Google Play Console
        /// </summary>
        [Required]
        public string SubscriptionId { get; set; }

        /// <summary>
        /// The purchase token for the subscription
        /// </summary>
        [Required]
        public string PurchaseToken { get; set; }

        /// <summary>
        /// The package name of the application
        /// </summary>
        [Required]
        public string PackageName { get; set; }

        /// <summary>
        /// The user ID who purchased the subscription
        /// </summary>
        [Required]
        public string UserId { get; set; }
    }

    /// <summary>
    /// Google Play Real-time Developer Notification (RTDN) webhook data
    /// </summary>
    public class GooglePlayNotification
    {
        /// <summary>
        /// The version of the notification
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// The package name of the application
        /// </summary>
        public string PackageName { get; set; }

        /// <summary>
        /// The timestamp of the event in milliseconds since Unix epoch
        /// </summary>
        public long EventTimeMillis { get; set; }

        /// <summary>
        /// One-time product notification data
        /// </summary>
        public OneTimeProductNotification OneTimeProductNotification { get; set; }

        /// <summary>
        /// Subscription notification data
        /// </summary>
        public SubscriptionNotification SubscriptionNotification { get; set; }

        /// <summary>
        /// Test notification data
        /// </summary>
        public TestNotification TestNotification { get; set; }
    }

    public class OneTimeProductNotification
    {
        /// <summary>
        /// The version of the notification
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// The type of notification
        /// </summary>
        public int NotificationType { get; set; }

        /// <summary>
        /// The purchase token for the one-time product
        /// </summary>
        public string PurchaseToken { get; set; }

        /// <summary>
        /// The SKU of the product
        /// </summary>
        public string Sku { get; set; }
    }

    public class SubscriptionNotification
    {
        /// <summary>
        /// The version of the notification
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// The type of notification
        /// </summary>
        public int NotificationType { get; set; }

        /// <summary>
        /// The purchase token for the subscription
        /// </summary>
        public string PurchaseToken { get; set; }

        /// <summary>
        /// The subscription ID
        /// </summary>
        public string SubscriptionId { get; set; }
    }

    public class TestNotification
    {
        /// <summary>
        /// The version of the test notification
        /// </summary>
        public string Version { get; set; }
    }

    /// <summary>
    /// Configuration settings for Google Play integration
    /// </summary>
    public class GooglePlaySettings
    {
        /// <summary>
        /// The package name of the Android application
        /// </summary>
        public string PackageName { get; set; }

        /// <summary>
        /// Path to the service account key file for Google Play API
        /// </summary>
        public string ServiceAccountKeyFile { get; set; }

        /// <summary>
        /// Application name for Google API client
        /// </summary>
        public string ApplicationName { get; set; }
    }

    /// <summary>
    /// Request to get localized pricing for a Google Play product
    /// </summary>
    public class GooglePlayProductPricingRequest
    {
        /// <summary>
        /// The product ID (SKU) from Google Play
        /// </summary>
        [Required]
        public string ProductId { get; set; }

        /// <summary>
        /// The package name of the application
        /// </summary>
        [Required]
        public string PackageName { get; set; }
    }

    /// <summary>
    /// Response containing localized pricing information for a Google Play product
    /// </summary>
    public class GooglePlayProductPricingResponse
    {
        /// <summary>
        /// The product ID (SKU)
        /// </summary>
        public string ProductId { get; set; }

        /// <summary>
        /// The product status (active, inactive, etc.)
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Purchase type (managedUser, managedGlobal, subscription)
        /// </summary>
        public string PurchaseType { get; set; }

        /// <summary>
        /// Default language code
        /// </summary>
        public string DefaultLanguage { get; set; }

        /// <summary>
        /// Localized product listings with pricing
        /// </summary>
        public Dictionary<string, LocalizedListing> Listings { get; set; }

        /// <summary>
        /// Pricing information per country/region
        /// </summary>
        public Dictionary<string, PriceInfo> Prices { get; set; }
    }

    /// <summary>
    /// Localized product listing information
    /// </summary>
    public class LocalizedListing
    {
        /// <summary>
        /// Localized title
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Localized description
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Price information for a specific country/currency
    /// </summary>
    public class PriceInfo
    {
        /// <summary>
        /// Price in micro-units (divide by 1,000,000 for actual price)
        /// </summary>
        public string PriceMicros { get; set; }

        /// <summary>
        /// Currency code (ISO 4217)
        /// </summary>
        public string Currency { get; set; }

        /// <summary>
        /// Formatted price string with currency symbol
        /// </summary>
        public string FormattedPrice { get; set; }
    }
}

