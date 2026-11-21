using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using Google;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using User = Coflnet.Payments.Models.User;

namespace Payments.Controllers;

public partial class CallbackController
{
    private const int SubscriptionRevoked = 12;
    private const int VoidedSubscription = 1;
    private const int VoidedProduct = 2;
    private const int QuantityBasedPartialRefund = 2;
    private readonly IConfigurationSection googlePlayWebhookConfig;
    internal Func<string, Task<GoogleJsonWebSignature.Payload>> ValidateGooglePlayPushToken { get; set; } =
        token => GoogleJsonWebSignature.ValidateAsync(token);

    /// <summary>Receives Google Play real-time developer notifications from Cloud Pub/Sub.</summary>
    [HttpPost("googlepay")]
    public async Task<IActionResult> GooglePlayWebhook()
    {
        try
        {
            if (!await IsGooglePlayPushAuthorized())
                return Unauthorized();

            var notification = await ReadGooglePlayNotification();
            if (notification?.PackageName != SubscriptionService.GooglePlayPackage)
                return Ok();
            if (notification.VoidedPurchaseNotification is { RefundType: QuantityBasedPartialRefund } voided)
            {
                _logger.LogWarning("Ignoring Google Play quantity-based partial refund for product type {ProductType}", voided.ProductType);
                return Ok();
            }

            _logger.LogInformation("Processing {GooglePlayNotification}", WebhookLogSummary.OfGooglePlay(notification));
            await HandleGooglePlayNotification(notification);
            return Ok();
        }
        catch (Exception e)
        {
            _logger.LogError("Google Play webhook failed ({ExceptionType}, HTTP {StatusCode})",
                e.GetType().Name, (int?)(e as GoogleApiException)?.HttpStatusCode);
            return StatusCode(500);
        }
    }

    private async Task<bool> IsGooglePlayPushAuthorized()
    {
        var audience = googlePlayWebhookConfig["PUBSUB_AUDIENCE"];
        var serviceAccount = googlePlayWebhookConfig["PUBSUB_SERVICE_ACCOUNT"];
        if (string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(serviceAccount))
        {
            _logger.LogWarning("Rejecting Google Play push because GOOGLEPAY:PUBSUB_AUDIENCE or GOOGLEPAY:PUBSUB_SERVICE_ACCOUNT is unset");
            return false;
        }
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var auth)
            || !auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(auth.Parameter))
            return false;
        try
        {
            var jwt = await ValidateGooglePlayPushToken(auth.Parameter);
            return jwt.Issuer is ("accounts.google.com" or "https://accounts.google.com")
                && jwt.AudienceAsList.Contains(audience, StringComparer.Ordinal)
                && jwt.EmailVerified && string.Equals(jwt.Email, serviceAccount, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is InvalidJwtException or FormatException or ArgumentException or System.Text.Json.JsonException or JsonException)
        {
            return false;
        }
    }

    private async Task<GooglePlayNotification> ReadGooglePlayNotification()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = JObject.Parse(await reader.ReadToEndAsync());
            if (body["message"] is JObject message && message["data"]?.Type == JTokenType.String)
                body = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(message.Value<string>("data"))));
            return body.ToObject<GooglePlayNotification>();
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            _logger.LogWarning("Ignoring malformed Google Play push payload");
            return null;
        }
    }

    private async Task HandleGooglePlayNotification(GooglePlayNotification notification)
    {
        if (notification.SubscriptionNotification is { } subscription)
            await subscriptionService.SyncGooglePlayNotification(subscription.PurchaseToken, revoked: subscription.NotificationType == SubscriptionRevoked);
        else if (notification.VoidedPurchaseNotification is { } voided)
            await HandleVoidedGooglePlayPurchase(voided, notification.EventTimeMillis);
        else if (notification.OneTimeProductNotification is { NotificationType: 1 } product)
            await HandleOneTimeProductNotification(product, notification.PackageName);
    }

    private async Task HandleVoidedGooglePlayPurchase(VoidedPurchaseNotification voided, long eventTimeMillis)
    {
        if (voided.ProductType is not (VoidedSubscription or VoidedProduct) || !await ConfirmGooglePlayVoid(voided, eventTimeMillis))
            return;
        if (voided.ProductType == VoidedSubscription)
        {
            await subscriptionService.SyncGooglePlayNotification(voided.PurchaseToken, voidedOrderId: voided.OrderId);
            return;
        }
        if (await transactionService.ApplyTopUpRefund(voided.OrderId, 1, 1, "googlepay") != null)
            await MarkPaymentRefunded(voided.OrderId, "googlepay");
    }

    private async Task<bool> ConfirmGooglePlayVoid(VoidedPurchaseNotification voided, long eventTimeMillis)
    {
        var hasPurchaseDetails = !string.IsNullOrWhiteSpace(voided.OrderId) && !string.IsNullOrWhiteSpace(voided.PurchaseToken);
        if (hasPurchaseDetails && await googlePlayService.IsVoidedPurchase(voided.OrderId, voided.PurchaseToken))
            return true;

        var now = DateTimeOffset.UtcNow;
        if (hasPurchaseDetails && eventTimeMillis >= now.AddHours(-24).ToUnixTimeMilliseconds()
            && eventTimeMillis <= now.AddMinutes(5).ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Google Play void confirmation is pending");
        _logger.LogWarning("Ignoring unconfirmed Google Play void for product type {ProductType}", voided.ProductType);
        return false;
    }

    private async Task HandleOneTimeProductNotification(OneTimeProductNotification notification, string packageName)
    {
        if (string.IsNullOrWhiteSpace(notification.Sku) || string.IsNullOrWhiteSpace(notification.PurchaseToken))
            return;
        ProductPurchase purchase;
        try
        {
            purchase = await googlePlayService.VerifyProductPurchaseAsync(packageName, notification.Sku, notification.PurchaseToken);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            _logger.LogWarning("Ignoring a Google Play notification for an unavailable purchase ({StatusCode})", e.HttpStatusCode);
            return;
        }
        if (purchase == null || purchase.PurchaseState != 0 || purchase.AcknowledgementState == 1 || string.IsNullOrWhiteSpace(purchase.OrderId))
            return;

        var payload = GooglePlayProductPayload(purchase);
        var userId = (string)(payload["user_id"] ?? payload["userId"]) ?? purchase.ObfuscatedExternalAccountId;
        if (userId == null && !string.IsNullOrWhiteSpace(purchase.DeveloperPayload) && !purchase.DeveloperPayload.Contains('{'))
            userId = purchase.DeveloperPayload;
        var user = string.IsNullOrWhiteSpace(userId) ? null : await db.Users.FirstOrDefaultAsync(u => u.ExternalId == userId);
        var product = await productService.GetTopupProductByProvider(notification.Sku, "googlepay", require: false);
        if (user == null || product == null)
        {
            _logger.LogWarning("No user or internal product for Google Play one-time purchase");
            return;
        }

        long.TryParse(payload["custom_amount"]?.ToString(), out var customAmount);
        await CreditGooglePlayProduct(purchase, product, user, customAmount);
    }

    private static JObject GooglePlayProductPayload(ProductPurchase purchase)
    {
        if (string.IsNullOrWhiteSpace(purchase.DeveloperPayload))
            return new JObject();
        try
        {
            return JObject.Parse(purchase.DeveloperPayload);
        }
        catch (JsonException)
        {
            return new JObject();
        }
    }

    private async Task CreditGooglePlayProduct(ProductPurchase purchase, TopUpProduct product, User user, long customAmount)
    {
        const string provider = "googlepay";
        var userId = user.ExternalId;
        if (await db.PaymentRecords.AnyAsync(p => p.Provider == provider && p.ExternalOrderId == purchase.OrderId))
            return;
        try
        {
            await transactionService.WithTransactionAsync(async (_, _) =>
            {
                await transactionService.AddTopUp(product.Id, userId, purchase.OrderId, customAmount);
                await paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = (double)product.Price, ProductId = product.Id.ToString(), ProductTitle = product.Title,
                    UserId = userId, Currency = product.CurrencyCode, PaymentMethod = provider, PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = purchase.OrderId, Timestamp = DateTime.UtcNow
                });
                db.PaymentRecords.Add(new PaymentRecord
                {
                    UserId = user.Id, ExternalUserId = userId, Country = user.Country, ZipCode = user.Zip, Locale = user.Locale,
                    GrossAmount = product.Price, Subtotal = product.Price, NetAmount = product.Price,
                    TaxRemittedByProcessor = true, Currency = product.CurrencyCode?.ToUpperInvariant() ?? "USD",
                    Provider = provider, PaymentMethod = provider, ExternalOrderId = purchase.OrderId, ProductSlug = product.Slug,
                    ProductId = product.Id, CoinAmount = customAmount, PaidAt = DateTime.UtcNow, RecordedAt = DateTime.UtcNow,
                    Status = PaymentRecordStatus.Confirmed
                });
                await db.SaveChangesAsync();
            });
        }
        catch (TransactionService.DupplicateTransactionException)
        {
            // App verification or another delivery has already credited this order.
        }
    }
}
