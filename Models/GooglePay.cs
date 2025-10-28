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
}
