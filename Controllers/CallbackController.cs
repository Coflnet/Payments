using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PayPalCheckoutSdk.Orders;
using PayPalHttp;
using Stripe;
using System.Linq;
using System.Runtime.Serialization;
using PayPalCheckoutSdk.Core;
using Stripe.Checkout;
using System.Security.Cryptography;
using System.Text;
using Coflnet.Payments.Models.LemonSqueezy;
using Newtonsoft.Json;

namespace Payments.Controllers
{
    /// <summary>
    /// External server side callbacks
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class CallbackController : ControllerBase
    {
        private readonly ILogger<CallbackController> _logger;
        private readonly PaymentContext db;
        private string signingSecret;
        private string lemonSqueezySecret;
        private TransactionService transactionService;
        private IPaymentEventProducer paymentEventProducer;
        private readonly PayPalHttpClient paypalClient;
        private readonly Coflnet.Payments.Services.SubscriptionService subscriptionService;
        private readonly GooglePlayService googlePlayService;
        private readonly Coflnet.Payments.Services.ProductService productService;
        private readonly GooglePayController _googlePayController;

        public CallbackController(
            IConfiguration config,
            ILogger<CallbackController> logger,
            PaymentContext context,
            TransactionService transactionService,
            PayPalHttpClient paypalClient,
            IPaymentEventProducer paymentEventProducer,
            Coflnet.Payments.Services.SubscriptionService subscriptionService,
            ILogger<GooglePayController> googlePayLogger,
            GooglePlayService googlePlayService,
            Coflnet.Payments.Services.ProductService productService)
        {
            _logger = logger;
            db = context;
            signingSecret = config["STRIPE:SIGNING_SECRET"];
            lemonSqueezySecret = config["LEMONSQUEEZY:SECRET"] ?? throw new Exception("Lemon Squeezy Secret not set");
            this.transactionService = transactionService;
            this.paypalClient = paypalClient;
            this.paymentEventProducer = paymentEventProducer;
            this.subscriptionService = subscriptionService;
            this.googlePlayService = googlePlayService;
            this.productService = productService;
            // instantiate GooglePayController to reuse its verification logic and keep a single implementation
            _googlePayController = new GooglePayController(googlePayLogger, googlePlayService, transactionService, paymentEventProducer, productService);
        }

        /// <summary>
        /// Webhook callback for stripe
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<IActionResult> CreateStripe()
        {

            _logger.LogInformation("received callback from stripe --");
            var syncIOFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }
            string json = "";
            try
            {
                json = new StreamReader(Request.Body).ReadToEnd();
                if (String.IsNullOrEmpty(json))
                    throw new ApiException("Json body is not set");

                var stripeEvent = EventUtility.ConstructEvent(
                    json,
                    Request.Headers["Stripe-Signature"],
                    signingSecret
                );
                _logger.LogInformation("stripe valiadted");
                _logger.LogInformation(json);

                if (stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    _logger.LogInformation("stripe checkout completed");
                    var session = stripeEvent.Data.Object as Stripe.Checkout.Session;

                    // Fulfill the purchase...
                    var productId = int.Parse(session.Metadata["productId"]);
                    int.TryParse(session.Metadata.GetValueOrDefault("coinAmount", "0"), out int coinAmount);
                    try
                    {
                        await transactionService.AddTopUp(productId, session.ClientReferenceId, session.PaymentIntentId, coinAmount);
                        var transaction = await db.PaymentRequests.Where(t => t.SessionId == session.Id).FirstOrDefaultAsync();
                        if (transaction != null)
                        {
                            transaction.State = PaymentRequest.Status.PAID;
                            transaction.SessionId = session.PaymentIntentId;
                            await db.SaveChangesAsync();
                        }
                    }
                    catch (TransactionService.DupplicateTransactionException)
                    {
                        // this already happened so was successful
                    }
                    await paymentEventProducer.ProduceEvent(new PaymentEvent
                    {
                        PayedAmount = (session.AmountTotal ?? throw new Exception("wtf need amount")) / 100.0,
                        ProductId = productId.ToString(),
                        UserId = session.ClientReferenceId,
                        Address = new Coflnet.Payments.Models.Address()
                        {
                            CountryCode = session.CustomerDetails.Address.Country,
                            PostalCode = session.CustomerDetails.Address.PostalCode,
                            City = session.CustomerDetails.Address.City,
                            Line1 = session.CustomerDetails.Address.Line1,
                            Line2 = session.CustomerDetails.Address.Line2
                        },
                        FullName = session.CustomerDetails.Name,
                        Email = session.CustomerDetails.Email,
                        Currency = session.Currency,
                        PaymentMethod = session.PaymentMethodTypes[0],
                        PaymentProvider = "stripe",
                        PaymentProviderTransactionId = session.PaymentIntentId,
                        Timestamp = session.Created
                    });
                }
                else if (stripeEvent.Type == Events.ChargeFailed)
                {
                    var charge = stripeEvent.Data.Object as Stripe.Charge;
                    var intentId = charge.PaymentIntentId;
                    _logger.LogInformation("stripe charge failed " + intentId);
                    var transaction = await db.PaymentRequests.Where(t => t.SessionId == intentId).FirstOrDefaultAsync();
                    if (transaction != null)
                    {
                        if (transaction.State == PaymentRequest.Status.FAILED)
                        {
                            // already failed once before tell stripe to expire the checkout session
                            var service = new SessionService();
                            var session = await service.ListAsync(new SessionListOptions { PaymentIntent = intentId });
                            if (session.Count() > 0)
                            {
                                _logger.LogInformation($"stripe charge failed again, canceling session {intentId} {session.FirstOrDefault().Id}");
                                await service.ExpireAsync(session.FirstOrDefault().Id);
                            }
                            else
                                _logger.LogInformation($"no session found for {intentId}");
                            return Ok();
                        }
                        transaction.State = PaymentRequest.Status.FAILED;
                        await db.SaveChangesAsync();
                    }
                }
                else if (stripeEvent.Type == Events.ChargeRefunded)
                {
                    var charge = stripeEvent.Data.Object as Stripe.Charge;
                    var intentId = charge.PaymentIntentId;
                    _logger.LogInformation("stripe charge refunded " + intentId);
                    var payment = await db.PaymentRequests.Where(t => t.SessionId == intentId).FirstOrDefaultAsync();
                    var transaction = await db.FiniteTransactions.Where(t => t.Reference == intentId).Select(t => new { UserId = t.User.ExternalId, t.Id }).FirstOrDefaultAsync();
                    if (transaction != null)
                    {
                        _logger.LogInformation($"reverting purchase {transaction.Id} from {transaction.UserId} because of refund");
                        await transactionService.RevertPurchase(transaction.UserId, transaction.Id);
                    }
                    if (payment != null)
                    {
                        payment.State = PaymentRequest.Status.REFUNDED;
                        await db.SaveChangesAsync();
                    }
                }
                else
                {
                    _logger.LogWarning("stripe is not comlete type of " + stripeEvent.Type);
                }

                return Ok();
            }
            catch (StripeException ex)
            {
                _logger.LogError($"Ran into exception for stripe callback {ex.Message} \n{ex.StackTrace} {json}");
                return StatusCode(400);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "stripe checkout");
                return StatusCode(400);
            }
        }

        [HttpPost]
        [Route("lemonsqueezy")]
        public async Task<IActionResult> LemonSqueezy([FromHeader(Name = "x-signature")] string signature)
        {
            var syncIOFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }
            var json = new StreamReader(Request.Body).ReadToEnd();
            // check hex signature hmac
            var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(lemonSqueezySecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();
            if (hashString != signature)
            {
                _logger.LogWarning($"lemonsqueezy signature mismatch {hashString} != {signature}");
                //    return StatusCode(400);
            }
            _logger.LogInformation("received callback from lemonsqueezy --\n{data}", json);
            var webhook = System.Text.Json.JsonSerializer.Deserialize<Coflnet.Payments.Models.LemonSqueezy.Webhook>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
            });
            var data = webhook.Data;
            var meta = webhook.Meta;
            if (meta.EventName == "order_created" && data.Attributes.Status == "paid")
            {
                if (webhook.Meta.CustomData.IsSubscription.Equals("true", StringComparison.OrdinalIgnoreCase)) // custom data can only be strings
                {
                    _logger.LogInformation("lemonsqueezy subscription payment, skipping");
                    return Ok();
                }
                await transactionService.AddTopUp(meta.CustomData.ProductId, meta.CustomData.UserId, data.Attributes.Identifier, meta.CustomData.CoinAmount);
                await db.SaveChangesAsync();
                _logger.LogInformation($"lemonsqueezy topup {meta.CustomData.ProductId} {meta.CustomData.UserId} {data.Attributes.Identifier} {meta.CustomData.CoinAmount}");
            }
            else if (meta.EventName == "order_refunded" && data.Attributes.Status == "refunded")
            {
                if (meta.CustomData.IsSubscription != null && meta.CustomData.IsSubscription.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    await RevertSubscriptionPayment(webhook);
                    return Ok();
                }
                await RevertTopUpWithReference(data.Attributes.Identifier);
            }
            else if (meta.EventName == "subscription_payment_success" && data.Attributes.Status == "paid")
                await subscriptionService.PaymentReceived(webhook);
            else if (meta.EventName == "subscription_updated" || meta.EventName == "subscription_created")
                await subscriptionService.UpdateSubscription(webhook);
            else if (meta.EventName == "subscription_payment_refunded")
            {
                await subscriptionService.RefundPayment(webhook);
            }
            else if (meta.EventName == "subscription_payment_failed")
            {
                _logger.LogInformation("Subscription payment failed for {userId} {productId}", meta.CustomData.UserId, meta.CustomData.ProductId);
            }
            else
            {
                _logger.LogWarning($"lemonsqueezy unknown type {meta.EventName}");
                return StatusCode(500);
            }
            return Ok();
        }

        private async Task RevertSubscriptionPayment(Webhook webhook)
        {
            var matchingSubscriptionPayment = await db.FiniteTransactions
                .Where(t => (t.Amount == webhook.Meta.CustomData.CoinAmount || t.Amount == -webhook.Meta.CustomData.CoinAmount)
                    && t.User.ExternalId == webhook.Meta.CustomData.UserId)
                .Include(t => t.Product)
                .OrderByDescending(t => t.Timestamp)
                .Take(10)
                .ToListAsync();
            var reverts = matchingSubscriptionPayment
            .Where(t => t.Product.Slug == "revert")
            .Select(t => long.Parse(t.Reference.Split(' ').Last())).ToList();

            var nonReverted = matchingSubscriptionPayment
                .Where(t => !reverts.Contains(t.Id) && t.Product.Slug != "revert")
                .OrderByDescending(t => t.Timestamp)
                .ToList();
            var createdDate = webhook.Data.Attributes.CreatedAt.ToString("yyyy-MM-dd");
            var purchase = nonReverted.Where(t => t.Amount == -webhook.Meta.CustomData.CoinAmount
                && t.Reference.Contains(createdDate)).FirstOrDefault();
            if (purchase == null)
            {
                _logger.LogWarning($"No matching purchase found for subscription payment {webhook.Meta.CustomData.UserId} {webhook.Meta.CustomData.ProductId} {createdDate}");
                return;
            }
            var topupSlug = purchase?.Reference + "-topup";
            // because the topup 
            await RevertTopUpWithReference(topupSlug);
            await RevertTopUpWithReference(purchase.Reference, true);
            _logger.LogInformation($"Reverted subscription payment for {webhook.Meta.CustomData.UserId} {webhook.Meta.CustomData.ProductId} {purchase?.Reference}");
        }

        /// <summary>
        /// accept callbacks from paypal
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Route("paypal")]
        public async Task<IActionResult> Paypal()
        {
            _logger.LogInformation("received callback from paypal --");
            var syncIOFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }
            string json = "";
            string referenceId = "";
            try
            {
                _logger.LogInformation("reading json");
                json = await new StreamReader(Request.Body).ReadToEndAsync();
                var webhookResult = Newtonsoft.Json.JsonConvert.DeserializeObject<PayPalWebhookData>(json);
                _logger.LogInformation(Newtonsoft.Json.JsonConvert.SerializeObject(webhookResult));
                if (webhookResult.EventType == "CHECKOUT.ORDER.APPROVED")
                {
                    var address = webhookResult.Resource.PurchaseUnits[0].ShippingDetail.AddressPortable;
                    var country = address.CountryCode;
                    var postalCode = address.PostalCode;
                    var state = address.AdminArea2;
                    var userId = webhookResult.Resource.PurchaseUnits[0].CustomId.Split(';')[2];
                    if (!DoWeSellto(country, postalCode))
                    {
                        var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
                        if (user != null)
                            user.Country = country;
                        else
                            _logger.LogWarning($"Didn't find user {userId} to update country {country}");
                        await db.SaveChangesAsync();
                        return Ok(); // ignore order
                    }
                    _logger.LogInformation($"received order from {userId} {country} {postalCode} {state} {json}");
                    var coinAmount = double.Parse(webhookResult.Resource.PurchaseUnits[0].CustomId.Split(';')[1]);
                    if ((state == "MD" || state == "KY") && country == "US" && coinAmount < 5000)
                    {
                        return Ok(); // ignore order
                    }
                    // check that there are not too many orders
                    if (await HasToManyTopups(userId, (decimal)coinAmount))
                    {
                        _logger.LogInformation($"too many orders for user {userId} aborting");
                        return Ok();
                    }
                    // completing order
                    await CompleteOrder(paypalClient, webhookResult.Resource.Id);
                    _logger.LogInformation("completed order " + webhookResult.Resource.Id);
                    return Ok();
                }
                else if (webhookResult.EventType == "PAYMENT.CAPTURE.COMPLETED")
                {
                    dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    var id = (string)data.resource.supplementary_data.related_ids.order_id;

                    var refundableId = webhookResult.Resource.Links.Where(l => l.Rel == "self").First().Href.Split('/').Last();
                    _logger.LogInformation("received confirmation for purchase " + id);
                    var existing = await db.FiniteTransactions.Where(t => t.Reference == refundableId).FirstOrDefaultAsync();
                    if (existing != null)
                    {
                        _logger.LogInformation($"already have transaction for {refundableId} {existing.Id}");
                        return Ok();
                    }
                    var transaction = await db.FiniteTransactions.Where(t => t.Reference == id).FirstOrDefaultAsync();
                    referenceId = refundableId;
                    if (transaction != null)
                    {
                        transaction.Reference = refundableId;
                        await db.SaveChangesAsync();
                        _logger.LogInformation($"updated transaction {transaction.Id} with refundable id {refundableId}");
                    }
                    else
                    {
                        _logger.LogInformation($"no transaction found for {id}");
                    }

                }
                else if (webhookResult.EventType == "PAYMENT.CAPTURE.REFUNDED")
                {
                    dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    var id = webhookResult.Resource.Links.Where(l => l.Rel == "up").First().Href.Split('/').Last();
                    FiniteTransaction transaction = await RevertTopUpWithReference(id);
                    _logger.LogInformation($"refunded payment, reverting topup {id} from {transaction.User.ExternalId} because of refund");
                    return Ok();
                }
                else
                {
                    _logger.LogWarning("paypal is not comlete type of " + webhookResult.EventType);
                    return Ok();
                }

                //3. Call PayPal to get the transaction
                PayPalHttp.HttpResponse response;
                try
                {
                    dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    var id = (string)data.resource.supplementary_data.related_ids.order_id;
                    OrdersGetRequest getRequest = new OrdersGetRequest(id);
                    _logger.LogInformation($"getting order  {id} from " + webhookResult.Resource.Id);
                    response = paypalClient.Execute(getRequest).Result;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "payPalPayment");
                    throw new ApiException("The provided orderId has not vaid payment asociated");
                }
                //4. Save the transaction in your database. Implement logic to save transaction to your database for future reference.
                var order = response.Result<PayPalCheckoutSdk.Orders.Order>();
                _logger.LogInformation("Retrieved Order Status");
                AmountWithBreakdown amount = order.PurchaseUnits[0].AmountWithBreakdown;
                _logger.LogInformation("Total Amount: {0} {1}", amount.CurrencyCode, amount.Value);

                if (order.Status != "COMPLETED")
                    throw new ApiException("The order is not yet completed");

                _logger.LogInformation("Status: {0}", order.Status);

                _logger.LogInformation("Order: {0}", Newtonsoft.Json.JsonConvert.SerializeObject(order));
                _logger.LogInformation("ReferenceId: {0}", referenceId ?? throw new Exception("no reference id"));
                //if (DateTime.Parse(order.PurchaseUnits[0].Payments.Captures[0].UpdateTime) < DateTime.UtcNow.Subtract(TimeSpan.FromHours(1)))
                //    throw new Exception("the provied order id is too old, please contact support for manual review");

                var transactionId = order.Links.Where(l => l.Rel == "self").First().Href.Split('/').Last();
                var product = order.PurchaseUnits[0];
                var topupInfo = product.CustomId.Split(';');
                _logger.LogInformation($"user {product.ReferenceId} purchased '{product.CustomId}' via PayPal {transactionId}");
                var exactCoinAmount = 0;
                if (topupInfo.Length >= 2)
                    int.TryParse(topupInfo[1], out exactCoinAmount);
                await transactionService.AddTopUp(int.Parse(topupInfo[0]), product.ReferenceId, referenceId, exactCoinAmount);

                await paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = double.Parse(amount.Value),
                    ProductId = topupInfo[0],
                    UserId = product.ReferenceId,
                    FullName = product.ShippingDetail.Name.FullName,
                    FirstName = order.Payer.Name.GivenName,
                    LastName = order.Payer.Name.Surname,
                    Email = order.Payer.Email,
                    Address = new Coflnet.Payments.Models.Address()
                    {
                        CountryCode = product.ShippingDetail.AddressPortable.CountryCode,
                        PostalCode = product.ShippingDetail.AddressPortable.PostalCode,
                        City = product.ShippingDetail.AddressPortable.AdminArea2,
                        Line1 = product.ShippingDetail.AddressPortable.AddressLine1,
                        Line2 = product.ShippingDetail.AddressPortable.AddressLine2
                    },
                    Currency = amount.CurrencyCode,
                    PaymentMethod = "paypal",
                    PaymentProvider = "paypal",
                    PaymentProviderTransactionId = transactionId,
                    Timestamp = string.IsNullOrEmpty(order.CreateTime) ? DateTime.UtcNow : DateTime.Parse(order.CreateTime)
                });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "paypal checkout");
                return StatusCode(400);
            }

            return Ok();
        }

        private async Task<FiniteTransaction> RevertTopUpWithReference(string id, bool revertTime = false)
        {
            var transaction = await db.FiniteTransactions.Where(t => t.Reference == id).Include(t => t.User).FirstOrDefaultAsync();
            await transactionService.RevertPurchase(transaction.User.ExternalId, transaction.Id, revertTime);
            return transaction;
        }

        internal async Task<bool> HasToManyTopups(string userId, decimal coinAmount)
        {
            return await db.FiniteTransactions.Where(t => t.User == db.Users.Where(u => u.ExternalId == userId).First()
                                        && t.Product.Type.HasFlag(Coflnet.Payments.Models.Product.ProductType.TOP_UP)
                                        && t.Amount == coinAmount
                                            && t.Timestamp > DateTime.UtcNow.AddDays(-1)).CountAsync() >= 2;
        }

        public static bool DoWeSellto(string country, string postalCode)
        {
            if (country == "GB" && (postalCode?.StartsWith("BT") ?? false))
                return false; // registration too complicated for northern ireland
            if (country == "AE")
                return false; // can't register for taxes as a foreigner
            var list = new string[] { "TR", "AE", "SA", "KR", "VN", "CL", "MX", "PE", "MD" };
            if (list.Contains(country))
                return false; // to much overhead to register for taxes
            return true;
        }

        private async Task CompleteOrder(PayPalCheckoutSdk.Core.PayPalHttpClient client, string id)
        {
            var request = new OrdersCaptureRequest(id);
            request.RequestBody(new OrderActionRequest());
            try
            {

                HttpResponse responsea = await client.Execute(request);
                var statusCode = responsea.StatusCode;
                var result = responsea.Result<PayPalCheckoutSdk.Orders.Order>();
                _logger.LogInformation("Status: {0}", result.Status);
                _logger.LogInformation("Capture Id: {0}", result.Id);
            }
            catch (PayPalHttp.HttpException e)
            {
                if (e.Message.Contains("ORDER_ALREADY_CAPTURED"))
                    return;
                _logger.LogError(e, "paypal order capture");
            }
        }

        /// <summary>
        /// Verifies and processes a Google Play product purchase
        /// </summary>
        /// <param name="request">The Google Play purchase verification request</param>
        /// <returns>Purchase verification response</returns>
        [HttpPost]
        [Route("googlepay/verify")]
        public async Task<ActionResult<GooglePlayPurchaseResponse>> VerifyGooglePlayPurchase([FromBody] GooglePlayPurchaseRequest request)
        {
            // Delegate to the centralized GooglePayController implementation to avoid duplicate logic
            return await _googlePayController.VerifyPurchase(request);
        }

        /// <summary>
        /// Webhook callback for Google Play Real-time Developer Notifications (RTDN)
        /// </summary>
        /// <returns>Status result</returns>
        [HttpPost]
        [Route("googlepay")]
        public async Task<IActionResult> GooglePlayWebhook()
        {
            try
            {
                _logger.LogInformation("Received Google Play RTDN webhook");

                string json;
                using (var reader = new StreamReader(Request.Body))
                {
                    json = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(json))
                {
                    _logger.LogWarning("Google Play webhook received empty body");
                    return BadRequest("Empty request body");
                }

                // Parse the notification
                var notification = JsonConvert.DeserializeObject<GooglePlayNotification>(json);

                if (notification == null)
                {
                    _logger.LogWarning("Failed to parse Google Play notification");
                    return BadRequest("Invalid notification format");
                }

                _logger.LogInformation("Processing Google Play notification for package {PackageName}\n{full}", notification.PackageName, json);

                // Handle one-time product notifications
                if (notification.OneTimeProductNotification != null)
                {
                    await HandleOneTimeProductNotification(notification.OneTimeProductNotification, notification.PackageName);
                }

                // Handle subscription notifications
                if (notification.SubscriptionNotification != null)
                {
                    await HandleSubscriptionNotification(notification.SubscriptionNotification, notification.PackageName);
                }

                // Handle test notifications
                if (notification.TestNotification != null)
                {
                    _logger.LogInformation("Received Google Play test notification");
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Google Play webhook");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Handles one-time product notifications from Google Play
        /// </summary>
        /// <param name="notification">The one-time product notification</param>
        /// <param name="packageName">The package name</param>
        private async Task HandleOneTimeProductNotification(OneTimeProductNotification notification, string packageName)
        {
            try
            {
                _logger.LogInformation("Handling one-time product notification for SKU {Sku}, type {NotificationType}",
                    notification.Sku, notification.NotificationType);

                switch (notification.NotificationType)
                {
                    case 1: // ONE_TIME_PRODUCT_PURCHASED
                        // Verify and process the purchase
                        var purchase = await googlePlayService.VerifyProductPurchaseAsync(
                            packageName,
                            notification.Sku,
                            notification.PurchaseToken);

                        // Extract user ID from DeveloperPayload or ObfuscatedAccountId
                        string userId = ExtractUserIdFromPurchase(purchase, notification.Sku);

                        if (!string.IsNullOrEmpty(userId))
                        {
                            await ProcessGooglePlayProductPurchase(notification.Sku, userId, purchase, notification.PurchaseToken);
                            _logger.LogInformation("One-time product purchased and credited: {Sku} for user {UserId}", notification.Sku, userId);
                        }
                        else
                        {
                            _logger.LogWarning("Could not extract user ID from Google Play purchase for SKU {Sku}", notification.Sku);
                        }
                        break;

                    case 2: // ONE_TIME_PRODUCT_CANCELED
                        _logger.LogInformation("One-time product canceled: {Sku}", notification.Sku);
                        // Handle cancellation if needed
                        break;

                    default:
                        _logger.LogWarning("Unknown one-time product notification type: {NotificationType}", notification.NotificationType);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling one-time product notification for SKU {Sku}", notification.Sku);
            }
        }

        /// <summary>
        /// Handles subscription notifications from Google Play
        /// </summary>
        /// <param name="notification">The subscription notification</param>
        /// <param name="packageName">The package name</param>
        private async Task HandleSubscriptionNotification(SubscriptionNotification notification, string packageName)
        {
            try
            {
                _logger.LogInformation("Handling subscription notification for subscription {SubscriptionId}, type {NotificationType}",
                    notification.SubscriptionId, notification.NotificationType);

                switch (notification.NotificationType)
                {
                    case 1: // SUBSCRIPTION_RECOVERED
                    case 2: // SUBSCRIPTION_RENEWED
                    case 4: // SUBSCRIPTION_PURCHASED
                        // Verify and process the subscription
                        var subscription = await googlePlayService.VerifySubscriptionPurchaseAsync(
                            packageName,
                            notification.SubscriptionId,
                            notification.PurchaseToken);

                        // Extract user ID from DeveloperPayload or ObfuscatedAccountId
                        string userId = ExtractUserIdFromSubscription(subscription, notification.SubscriptionId);

                        if (!string.IsNullOrEmpty(userId))
                        {
                            await ProcessGooglePlaySubscriptionPurchase(notification.SubscriptionId, userId, subscription, notification.PurchaseToken, notification.NotificationType);
                            _logger.LogInformation("Subscription event processed and credited: {SubscriptionId}, type: {NotificationType} for user {UserId}",
                                notification.SubscriptionId, notification.NotificationType, userId);
                        }
                        else
                        {
                            _logger.LogWarning("Could not extract user ID from Google Play subscription for ID {SubscriptionId}", notification.SubscriptionId);
                        }
                        break;

                    case 3: // SUBSCRIPTION_CANCELED
                        _logger.LogInformation("Subscription canceled: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription cancellation
                        break;

                    case 5: // SUBSCRIPTION_ON_HOLD
                        _logger.LogInformation("Subscription on hold: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription on hold
                        break;

                    case 6: // SUBSCRIPTION_IN_GRACE_PERIOD
                        _logger.LogInformation("Subscription in grace period: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription in grace period
                        break;

                    case 7: // SUBSCRIPTION_RESTARTED
                        _logger.LogInformation("Subscription restarted: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription restart
                        break;

                    case 8: // SUBSCRIPTION_PRICE_CHANGE_CONFIRMED
                        _logger.LogInformation("Subscription price change confirmed: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription price change
                        break;

                    case 9: // SUBSCRIPTION_DEFERRED
                        _logger.LogInformation("Subscription deferred: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription deferral
                        break;

                    case 10: // SUBSCRIPTION_PAUSED
                        _logger.LogInformation("Subscription paused: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription pause
                        break;

                    case 11: // SUBSCRIPTION_PAUSE_SCHEDULE_CHANGED
                        _logger.LogInformation("Subscription pause schedule changed: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription pause schedule change
                        break;

                    case 12: // SUBSCRIPTION_REVOKED
                        _logger.LogInformation("Subscription revoked: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription revocation
                        break;

                    case 13: // SUBSCRIPTION_EXPIRED
                        _logger.LogInformation("Subscription expired: {SubscriptionId}", notification.SubscriptionId);
                        // Handle subscription expiration
                        break;

                    default:
                        _logger.LogWarning("Unknown subscription notification type: {NotificationType}", notification.NotificationType);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling subscription notification for subscription {SubscriptionId}", notification.SubscriptionId);
            }
        }

        /// <summary>
        /// Extracts user ID from Google Play product purchase data
        /// </summary>
        /// <param name="purchase">The product purchase data</param>
        /// <param name="sku">The product SKU</param>
        /// <returns>User ID if found, null otherwise</returns>
        private string ExtractUserIdFromPurchase(Google.Apis.AndroidPublisher.v3.Data.ProductPurchase purchase, string sku)
        {
            try
            {
                // First try to extract from DeveloperPayload (custom data passed during purchase)
                if (!string.IsNullOrEmpty(purchase.DeveloperPayload))
                {
                    // DeveloperPayload might contain JSON with user_id
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(purchase.DeveloperPayload);
                        if (payload.ContainsKey("user_id"))
                        {
                            return payload["user_id"].ToString();
                        }
                        if (payload.ContainsKey("userId"))
                        {
                            return payload["userId"].ToString();
                        }
                    }
                    catch
                    {
                        // If not JSON, check if it's a direct user ID
                        if (!purchase.DeveloperPayload.Contains("{") && purchase.DeveloperPayload.Length > 0)
                        {
                            return purchase.DeveloperPayload;
                        }
                    }
                }

                // Fallback to ObfuscatedAccountId if available
                if (!string.IsNullOrEmpty(purchase.ObfuscatedExternalAccountId))
                {
                    return purchase.ObfuscatedExternalAccountId;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting user ID from Google Play product purchase for SKU {Sku}", sku);
                return null;
            }
        }

        /// <summary>
        /// Extracts user ID from Google Play subscription purchase data
        /// </summary>
        /// <param name="subscription">The subscription purchase data</param>
        /// <param name="subscriptionId">The subscription ID</param>
        /// <returns>User ID if found, null otherwise</returns>
        private string ExtractUserIdFromSubscription(Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchase subscription, string subscriptionId)
        {
            try
            {
                // First try to extract from DeveloperPayload (custom data passed during purchase)
                if (!string.IsNullOrEmpty(subscription.DeveloperPayload))
                {
                    // DeveloperPayload might contain JSON with user_id
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(subscription.DeveloperPayload);
                        if (payload.ContainsKey("user_id"))
                        {
                            return payload["user_id"].ToString();
                        }
                        if (payload.ContainsKey("userId"))
                        {
                            return payload["userId"].ToString();
                        }
                    }
                    catch
                    {
                        // If not JSON, check if it's a direct user ID
                        if (!subscription.DeveloperPayload.Contains("{") && subscription.DeveloperPayload.Length > 0)
                        {
                            return subscription.DeveloperPayload;
                        }
                    }
                }

                // Fallback to ObfuscatedAccountId if available
                if (!string.IsNullOrEmpty(subscription.ObfuscatedExternalAccountId))
                {
                    return subscription.ObfuscatedExternalAccountId;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting user ID from Google Play subscription for ID {SubscriptionId}", subscriptionId);
                return null;
            }
        }

        /// <summary>
        /// Processes a Google Play product purchase and credits the user account
        /// </summary>
        /// <param name="sku">The product SKU</param>
        /// <param name="userId">The user ID</param>
        /// <param name="purchase">The purchase data</param>
        /// <param name="purchaseToken">The purchase token</param>
        private async Task ProcessGooglePlayProductPurchase(string sku, string userId, Google.Apis.AndroidPublisher.v3.Data.ProductPurchase purchase, string purchaseToken)
        {
            try
            {
                // Check if purchase is already processed
                if (purchase.AcknowledgementState == 1) // Already acknowledged
                {
                    _logger.LogInformation("Google Play product purchase already acknowledged for SKU {Sku}, user {UserId}", sku, userId);
                    return;
                }

                // Map Google Play product to internal product using database
                TopUpProduct product;
                try
                {
                    product = await productService.GetTopupProductByProvider(sku, "googlepay");
                }
                catch (ApiException)
                {
                    _logger.LogError("No internal product found for Google Play product {Sku}", sku);
                    return;
                }

                // Extract custom amount from DeveloperPayload if available
                long customAmount = 0;
                if (!string.IsNullOrEmpty(purchase.DeveloperPayload))
                {
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(purchase.DeveloperPayload);
                        if (payload.ContainsKey("custom_amount"))
                        {
                            long.TryParse(payload["custom_amount"].ToString(), out customAmount);
                        }
                    }
                    catch
                    {
                        // Ignore payload parsing errors for custom amount
                    }
                }

                // Process the transaction
                await transactionService.AddTopUp(product.Id, userId, purchase.OrderId, customAmount);

                // Send payment event
                await paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = (double)product.Price,
                    ProductId = product.Id.ToString(),
                    UserId = userId,
                    Currency = product.CurrencyCode,
                    PaymentMethod = "googlepay",
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = purchase.OrderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play product purchase for SKU {Sku}, user {UserId}, orderId {OrderId}",
                    sku, userId, purchase.OrderId);
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogInformation("Duplicate transaction for Google Play product {Sku}, user {UserId}", sku, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Google Play product purchase for SKU {Sku}, user {UserId}", sku, userId);
            }
        }

        /// <summary>
        /// Processes a Google Play subscription purchase and credits the user account
        /// </summary>
        /// <param name="subscriptionId">The subscription ID</param>
        /// <param name="userId">The user ID</param>
        /// <param name="subscription">The subscription data</param>
        /// <param name="purchaseToken">The purchase token</param>
        /// <param name="notificationType">The notification type</param>
        private async Task ProcessGooglePlaySubscriptionPurchase(string subscriptionId, string userId, Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchase subscription, string purchaseToken, int notificationType)
        {
            try
            {
                // Map Google Play subscription to internal product using database
                TopUpProduct product;
                try
                {
                    product = await productService.GetTopupProductByProvider(subscriptionId, "googlepay");
                }
                catch (ApiException)
                {
                    _logger.LogError("No internal product found for Google Play subscription {SubscriptionId}", subscriptionId);
                    return;
                }

                // Extract custom amount from DeveloperPayload if available
                long customAmount = 0;
                if (!string.IsNullOrEmpty(subscription.DeveloperPayload))
                {
                    try
                    {
                        var payload = JsonConvert.DeserializeObject<Dictionary<string, object>>(subscription.DeveloperPayload);
                        if (payload.ContainsKey("custom_amount"))
                        {
                            long.TryParse(payload["custom_amount"].ToString(), out customAmount);
                        }
                    }
                    catch
                    {
                        // Ignore payload parsing errors for custom amount
                    }
                }

                // Create a unique order ID for subscription payments
                var orderId = $"gp_sub_{subscriptionId}_{subscription.StartTimeMillis}_{notificationType}";

                // Process the transaction
                await transactionService.AddTopUp(product.Id, userId, orderId, customAmount);

                // Send payment event
                await paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = (double)product.Price,
                    ProductId = product.Id.ToString(),
                    UserId = userId,
                    Currency = product.CurrencyCode,
                    PaymentMethod = "googlepay",
                    PaymentProvider = "Google Play",
                    PaymentProviderTransactionId = orderId,
                    Timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("Successfully processed Google Play subscription purchase for ID {SubscriptionId}, user {UserId}, orderId {OrderId}",
                    subscriptionId, userId, orderId);
            }
            catch (TransactionService.DupplicateTransactionException)
            {
                _logger.LogInformation("Duplicate transaction for Google Play subscription {SubscriptionId}, user {UserId}", subscriptionId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Google Play subscription purchase for ID {SubscriptionId}, user {UserId}", subscriptionId, userId);
            }
        }

        /// <summary>
        /// PayPal webhook data structure
        /// </summary>
        [DataContract]
        public class PayPalWebhookData
        {
            /// <summary>
            /// Webhook ID
            /// </summary>
            [DataMember(Name = "id")]
            public string Id { get; set; }

            /// <summary>
            /// Webhook creation time
            /// </summary>
            [DataMember(Name = "create_time")]
            public DateTime CreateTime;

            /// <summary>
            /// Event type
            /// </summary>
            [DataMember(Name = "event_type")]
            public string EventType;

            /// <summary>
            /// Resource data
            /// </summary>
            [DataMember(Name = "resource")]
            public PayPalCheckoutSdk.Orders.Order Resource;
        }
    }
}
