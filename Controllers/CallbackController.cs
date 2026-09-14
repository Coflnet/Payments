using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Models.CoinGate;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PayPalCheckoutSdk.Orders;
using PayPalHttp;
using Stripe;
using PaymentRecord = Coflnet.Payments.Models.PaymentRecord;
using System.Linq;
using System.Runtime.Serialization;
using PayPalCheckoutSdk.Core;
using Stripe.Checkout;
using System.Security.Cryptography;
using System.Text;
using Coflnet.Payments.Models.LemonSqueezy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net.Http;

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
        private readonly string paypalWebhookId;
        private readonly Coflnet.Payments.Services.SubscriptionService subscriptionService;
        private readonly GooglePlayService googlePlayService;
        private readonly Coflnet.Payments.Services.ProductService productService;
        private readonly GooglePayController _googlePayController;
        private readonly CreatorCodeService _creatorCodeService;
        private readonly CoinGateService _coinGateService;

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
            Coflnet.Payments.Services.ProductService productService,
            CreatorCodeService creatorCodeService,
            CoinGateService coinGateService)
        {
            _logger = logger;
            db = context;
            signingSecret = config["STRIPE:SIGNING_SECRET"];
            lemonSqueezySecret = config["LEMONSQUEEZY:SECRET"];
            if (string.IsNullOrWhiteSpace(lemonSqueezySecret))
                throw new InvalidOperationException("Lemon Squeezy webhook secret not set");
            this.transactionService = transactionService;
            this.paypalClient = paypalClient;
            paypalWebhookId = config["PAYPAL:WEBHOOK_ID"];
            this.paymentEventProducer = paymentEventProducer;
            this.subscriptionService = subscriptionService;
            this._creatorCodeService = creatorCodeService;
            this._coinGateService = coinGateService;
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

                if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted
                    || stripeEvent.Type == EventTypes.CheckoutSessionAsyncPaymentSucceeded)
                {
                    _logger.LogInformation("stripe checkout completed");
                    var session = stripeEvent.Data.Object as Stripe.Checkout.Session;

                    // Fulfill the purchase...
                    var productId = int.Parse(session.Metadata["productId"]);
                    int.TryParse(session.Metadata.GetValueOrDefault("coinAmount", "0"), out int coinAmount);
                    var paymentIntentService = new PaymentIntentService();
                    var paymentIntent = await paymentIntentService.GetAsync(
                        session.PaymentIntentId,
                        new PaymentIntentGetOptions { Expand = new List<string> { "payment_method" } });
                    if (paymentIntent.Status == "processing")
                        return Ok(); // asynchronous payment methods emit a succeeded event later

                    var providerCountry = GetStripePaymentCountry(paymentIntent.PaymentMethod);
                    var paymentCountry = providerCountry
                        ?? session.Metadata.GetValueOrDefault("fallbackCountry")?.ToUpperInvariant();
                    if (paymentCountry == null || !DoWeSellto(paymentCountry, null))
                    {
                        if (paymentIntent.Status == "requires_capture")
                        {
                            await paymentIntentService.CancelAsync(paymentIntent.Id);
                            _logger.LogWarning(
                                "Stripe payment rejected before capture: payment country {Country} is unavailable or not accepted",
                                paymentCountry ?? "UNKNOWN");
                            var request = await db.PaymentRequests.FirstOrDefaultAsync(t => t.SessionId == session.Id);
                            if (request != null)
                            {
                                request.State = PaymentRequest.Status.FAILED;
                                await db.SaveChangesAsync();
                            }
                            return Ok();
                        }
                        if (paymentIntent.Status == "canceled")
                            return Ok();
                        if (paymentIntent.Status != "succeeded")
                            throw new ApiException($"Stripe payment is not capturable ({paymentIntent.Status})");
                        if (paymentCountry != null)
                        {
                            _logger.LogWarning(
                                "Stripe payment {PaymentIntentId} from {Country} requires a manual refund",
                                paymentIntent.Id,
                                paymentCountry);
                            var request = await db.PaymentRequests.FirstOrDefaultAsync(
                                t => t.SessionId == session.Id || t.SessionId == paymentIntent.Id);
                            if (request != null)
                            {
                                request.State = PaymentRequest.Status.REFUND_PENDING;
                                request.SessionId = paymentIntent.Id;
                                await db.SaveChangesAsync();
                            }
                            else
                                _logger.LogError(
                                    "No payment request found for Stripe payment {PaymentIntentId} requiring a manual refund",
                                    paymentIntent.Id);
                            return Ok();
                        }
                        _logger.LogWarning(
                            "Legacy Stripe payment has no country and was already captured; fulfilling it to avoid withholding a paid purchase");
                    }
                    else if (paymentIntent.Status == "requires_capture")
                        await paymentIntentService.CaptureAsync(paymentIntent.Id);
                    else if (paymentIntent.Status != "succeeded")
                        throw new ApiException($"Stripe payment is not capturable ({paymentIntent.Status})");

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
                            CountryCode = paymentCountry,
                            PostalCode = session.CustomerDetails.Address.PostalCode,
                            City = session.CustomerDetails.Address.City,
                            Line1 = session.CustomerDetails.Address.Line1,
                            Line2 = session.CustomerDetails.Address.Line2
                        },
                        FullName = session.CustomerDetails.Name,
                        Email = session.CustomerDetails.Email,
                        Currency = session.Currency,
                        PaymentMethod = paymentIntent.PaymentMethod?.Type ?? "unknown",
                        PaymentProvider = "stripe",
                        PaymentProviderTransactionId = session.PaymentIntentId,
                        Timestamp = session.Created
                    });

                    // Record for tax compliance
                    var stripeUser = await db.Users.Where(u => u.ExternalId == session.ClientReferenceId).FirstOrDefaultAsync();
                    if (stripeUser != null && stripeUser.Country != paymentCountry)
                    {
                        stripeUser.Country = paymentCountry;
                        await db.SaveChangesAsync();
                    }
                    await RecordPayment(new PaymentRecord
                    {
                        UserId = stripeUser?.Id ?? 0,
                        ExternalUserId = session.ClientReferenceId,
                        Country = paymentCountry,
                        ZipCode = session.CustomerDetails?.Address?.PostalCode,
                        City = session.CustomerDetails?.Address?.City,
                        State = session.CustomerDetails?.Address?.State,
                        GrossAmount = (session.AmountTotal ?? 0) / 100m,
                        Subtotal = (session.AmountSubtotal ?? session.AmountTotal ?? 0) / 100m,
                        DiscountAmount = 0, // Stripe handles coupons internally
                        TaxAmount = 0, // Stripe does not remit tax for us
                        TaxRemittedByProcessor = false,
                        NetAmount = (session.AmountTotal ?? 0) / 100m,
                        ProcessorFee = 0, // Not available in webhook; reconcile from Stripe dashboard
                        Currency = session.Currency?.ToUpper() ?? "USD",
                        Provider = "stripe",
                        PaymentMethod = paymentIntent.PaymentMethod?.Type ?? "unknown",
                        ExternalOrderId = session.Id,
                        ExternalTransactionId = session.PaymentIntentId,
                        ProductSlug = productId.ToString(),
                        ProductId = productId,
                        CoinAmount = coinAmount,
                        PaidAt = session.Created,
                        Status = PaymentRecordStatus.Confirmed,
                        BuyerEmail = session.CustomerDetails?.Email,
                        BuyerName = session.CustomerDetails?.Name,
                        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                    });
                }
                else if (stripeEvent.Type == EventTypes.ChargeFailed)
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
                else if (stripeEvent.Type == EventTypes.ChargeRefunded)
                {
                    var charge = stripeEvent.Data.Object as Stripe.Charge;
                    var intentId = charge.PaymentIntentId;
                    _logger.LogInformation("stripe charge refunded " + intentId);
                    var payment = await db.PaymentRequests.Where(t => t.SessionId == intentId).FirstOrDefaultAsync();
                    await transactionService.ApplyTopUpRefund(intentId, charge.Amount, charge.AmountRefunded, "stripe");
                    if (payment != null && charge.AmountRefunded >= charge.Amount)
                    {
                        payment.State = PaymentRequest.Status.REFUNDED;
                        await db.SaveChangesAsync();
                    }
                    await MarkPaymentRefunded(intentId, "stripe", charge.AmountRefunded / 100m, charge.AmountRefunded >= charge.Amount);
                }
                else
                {
                    _logger.LogWarning("stripe is not comlete type of " + stripeEvent.Type);
                }

                return Ok();
            }
            catch (StripeException ex)
            {
                if (StripeErrorClassifier.IsCredentialError(ex.HttpStatusCode))
                {
                    PaymentMetrics.StripeCredentialErrors.Inc();
                    _logger.LogCritical(
                        ex,
                        "Stripe callback processing was rejected by Stripe credentials (HTTP {StatusCode}, Stripe code {StripeCode}, request {RequestLogUrl}); returning 500 so Stripe retries",
                        ex.HttpStatusCode,
                        ex.StripeError?.Code,
                        ex.StripeError?.RequestLogUrl);
                    return StatusCode(500);
                }
                _logger.LogError(ex, "Stripe callback failed for payload {Payload}", json);
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
            if (string.IsNullOrWhiteSpace(signature))
            {
                _logger.LogWarning("Rejected Lemon Squeezy callback without a signature");
                return Unauthorized();
            }

            byte[] providedSignature;
            try
            {
                providedSignature = Convert.FromHexString(signature);
            }
            catch (FormatException)
            {
                _logger.LogWarning("Rejected Lemon Squeezy callback with a malformed signature");
                return Unauthorized();
            }

            using var body = new MemoryStream();
            await Request.Body.CopyToAsync(body);
            var payload = body.ToArray();
            var expectedSignature = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(lemonSqueezySecret),
                payload);
            if (providedSignature.Length != expectedSignature.Length
                || !CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            {
                _logger.LogWarning("Rejected Lemon Squeezy callback with an invalid signature");
                return Unauthorized();
            }

            _logger.LogInformation("Received verified callback from Lemon Squeezy");
            var webhook = System.Text.Json.JsonSerializer.Deserialize<Coflnet.Payments.Models.LemonSqueezy.Webhook>(payload, new System.Text.Json.JsonSerializerOptions
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
                
                // Process creator code if provided
                if (!string.IsNullOrWhiteSpace(meta.CustomData.CreatorCode))
                {
                    try
                    {
                        var creatorCode = await _creatorCodeService.ValidateCreatorCodeAsync(meta.CustomData.CreatorCode);
                        if (creatorCode != null)
                        {
                            // Calculate discount and revenue
                            var originalPrice = data.Attributes.Total / 100m; // Convert from cents
                            var currency = data.Attributes.Currency.ToUpper();
                            var discountAmount = originalPrice * (creatorCode.DiscountPercent / 100m);
                            var finalPrice = originalPrice - discountAmount;
                            var creatorRevenue = finalPrice * (creatorCode.RevenueSharePercent / 100m);
                            
                            // Record the revenue
                            await _creatorCodeService.RecordRevenueAsync(
                                creatorCode.Id,
                                meta.CustomData.UserId,
                                meta.CustomData.ProductId,
                                originalPrice,
                                discountAmount,
                                finalPrice,
                                creatorRevenue,
                                currency,
                                data.Attributes.Identifier
                            );
                            
                            _logger.LogInformation("Creator code {Code} applied: discount={Discount}%, revenue={Revenue} {Currency}",
                                creatorCode.Code, creatorCode.DiscountPercent, creatorRevenue, currency);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process creator code {Code}", meta.CustomData.CreatorCode);
                        // Continue processing the transaction even if creator code fails
                    }
                }
                
                await transactionService.AddTopUp(meta.CustomData.ProductId, meta.CustomData.UserId, data.Attributes.Identifier, meta.CustomData.CoinAmount);
                await paymentEventProducer.ProduceEvent(new PaymentEvent
                {
                    PayedAmount = data.Attributes.Total / 100.0,
                    ProductId = meta.CustomData.ProductId.ToString(),
                    UserId = meta.CustomData.UserId,
                    Currency = data.Attributes.Currency,
                    PaymentMethod = data.Attributes.PaymentProcessor ?? "card",
                    PaymentProvider = "lemonsqueezy",
                    PaymentProviderTransactionId = data.Attributes.Identifier,
                    Timestamp = data.Attributes.CreatedAt
                });
                await db.SaveChangesAsync();
                _logger.LogInformation($"lemonsqueezy topup {meta.CustomData.ProductId} {meta.CustomData.UserId} {data.Attributes.Identifier} {meta.CustomData.CoinAmount}");

                // Record for tax compliance — LemonSqueezy is Merchant of Record and remits tax
                var lsUser = await db.Users.Where(u => u.ExternalId == meta.CustomData.UserId).FirstOrDefaultAsync();
                var lsCreatorCode = !string.IsNullOrWhiteSpace(meta.CustomData.CreatorCode) ? meta.CustomData.CreatorCode : null;
                var lsCreatorDiscount = 0m;
                if (lsCreatorCode != null)
                {
                    try
                    {
                        var cc = await _creatorCodeService.ValidateCreatorCodeAsync(lsCreatorCode);
                        if (cc != null)
                            lsCreatorDiscount = (data.Attributes.Total / 100m) * (cc.DiscountPercent / 100m);
                    }
                    catch { /* already logged */ }
                }
                await RecordPayment(new PaymentRecord
                {
                    UserId = lsUser?.Id ?? 0,
                    ExternalUserId = meta.CustomData.UserId,
                    Country = lsUser?.Country,
                    ZipCode = lsUser?.Zip,
                    GrossAmount = data.Attributes.Total / 100m,
                    Subtotal = data.Attributes.Subtotal / 100m,
                    DiscountAmount = data.Attributes.DiscountTotal / 100m,
                    TaxAmount = data.Attributes.Tax / 100m,
                    TaxName = data.Attributes.TaxName,
                    TaxRate = data.Attributes.TaxRate,
                    TaxRemittedByProcessor = true, // LS is Merchant of Record
                    NetAmount = (data.Attributes.Total - data.Attributes.Tax) / 100m,
                    ProcessorFee = 0, // LS bundles fees into their cut
                    CreatorCode = lsCreatorCode,
                    CreatorCodeDiscount = lsCreatorDiscount,
                    Currency = data.Attributes.Currency?.ToUpper() ?? "USD",
                    Provider = "lemonsqueezy",
                    PaymentMethod = data.Attributes.PaymentProcessor ?? "card",
                    ExternalOrderId = data.Attributes.Identifier,
                    ExternalTransactionId = data.Id,
                    ProductSlug = meta.CustomData.ProductId.ToString(),
                    ProductId = meta.CustomData.ProductId,
                    CoinAmount = meta.CustomData.CoinAmount,
                    PaidAt = data.Attributes.CreatedAt,
                    Status = PaymentRecordStatus.Confirmed,
                    BuyerEmail = data.Attributes.UserEmail,
                    BuyerName = data.Attributes.UserName,
                    Locale = lsUser?.Locale,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    IsSubscriptionPayment = false
                });
            }
            else if (meta.EventName == "order_refunded"
                && (data.Attributes.Status == "refunded"
                    || data.Attributes.Status == "partial_refund"
                    || data.Attributes.Refunded
                    || data.Attributes.RefundedAmount > 0))
            {
                var refundedAmount = data.Attributes.RefundedAmount;
                var isFullRefund = data.Attributes.Status == "refunded" || data.Attributes.Refunded;
                if (isFullRefund && refundedAmount <= 0)
                    refundedAmount = data.Attributes.Total;

                if (refundedAmount <= 0 || data.Attributes.Total <= 0)
                {
                    _logger.LogWarning(
                        "Ignoring Lemon Squeezy refund {OrderId} with invalid amounts: refunded={RefundedAmount}, total={Total}",
                        data.Attributes.Identifier,
                        refundedAmount,
                        data.Attributes.Total);
                    return Ok();
                }

                isFullRefund = isFullRefund || refundedAmount >= data.Attributes.Total;
                if (meta.CustomData?.IsSubscription?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (isFullRefund)
                    {
                        await RevertSubscriptionPayment(webhook);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Recorded partial refund for subscription order {OrderId}; subscription access is unchanged",
                            data.Attributes.Identifier);
                    }
                    await MarkPaymentRefunded(data.Attributes.Identifier, "lemonsqueezy", refundedAmount / 100m, isFullRefund);
                    return Ok();
                }

                var balanceDeduction = await transactionService.ApplyTopUpRefund(
                    data.Attributes.Identifier,
                    data.Attributes.Total,
                    refundedAmount, "lemonsqueezy");
                if (balanceDeduction.HasValue)
                {
                    _logger.LogInformation(
                        "Applied Lemon Squeezy refund for order {OrderId}: deducted {BalanceAmount} balance (refunded {RefundedAmount}/{TotalAmount} cents)",
                        data.Attributes.Identifier,
                        balanceDeduction.Value,
                        refundedAmount,
                        data.Attributes.Total);
                }
                else
                {
                    _logger.LogWarning(
                        "No top-up transaction found for Lemon Squeezy refund order {OrderId}; balance was not changed",
                        data.Attributes.Identifier);
                }
                await MarkPaymentRefunded(data.Attributes.Identifier, "lemonsqueezy", refundedAmount / 100m, isFullRefund);
            }
            else if ((meta.EventName is "subscription_payment_success" or "subscription_payment_recovered") && data.Attributes.Status == "paid")
            {
                if (await db.PaymentRecords.AnyAsync(p => p.Provider == "lemonsqueezy"
                    && p.IsSubscriptionPayment && p.ExternalTransactionId == data.Id))
                    return Ok();
                var effectiveCustomData = await subscriptionService.PaymentReceived(webhook);
                var fullyRefunded = await db.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == data.Id);
                if (data.Attributes.Total > 0)
                {
                    await paymentEventProducer.ProduceEvent(new PaymentEvent
                    {
                        PayedAmount = data.Attributes.Total / 100.0,
                        ProductId = effectiveCustomData.ProductId.ToString(),
                        UserId = effectiveCustomData.UserId,
                        Currency = data.Attributes.Currency,
                        PaymentMethod = data.Attributes.PaymentProcessor ?? "card",
                        PaymentProvider = "lemonsqueezy",
                        PaymentProviderTransactionId = data.Attributes.Identifier ?? "ls-invoice-" + data.Id,
                        Timestamp = data.Attributes.CreatedAt
                    });
                }
                // Record subscription renewal payment for tax compliance
                var subUser = await db.Users.Where(u => u.ExternalId == effectiveCustomData.UserId).FirstOrDefaultAsync();
                await RecordPayment(new PaymentRecord
                {
                    UserId = subUser?.Id ?? 0,
                    ExternalUserId = effectiveCustomData.UserId,
                    Country = subUser?.Country,
                    ZipCode = subUser?.Zip,
                    GrossAmount = data.Attributes.Total / 100m,
                    Subtotal = data.Attributes.Subtotal / 100m,
                    DiscountAmount = data.Attributes.DiscountTotal / 100m,
                    TaxAmount = data.Attributes.Tax / 100m,
                    TaxName = data.Attributes.TaxName,
                    TaxRate = data.Attributes.TaxRate,
                    TaxRemittedByProcessor = true,
                    NetAmount = (data.Attributes.Total - data.Attributes.Tax) / 100m,
                    Currency = data.Attributes.Currency?.ToUpper() ?? "USD",
                    Provider = "lemonsqueezy",
                    PaymentMethod = data.Attributes.PaymentProcessor ?? "card",
                    ExternalOrderId = data.Attributes.Identifier ?? "ls-invoice-" + data.Id,
                    ExternalTransactionId = data.Id,
                    ProductSlug = effectiveCustomData.ProductId.ToString(),
                    ProductId = effectiveCustomData.ProductId,
                    CoinAmount = effectiveCustomData.CoinAmount,
                    PaidAt = data.Attributes.CreatedAt,
                    Status = fullyRefunded ? PaymentRecordStatus.Refunded : PaymentRecordStatus.Confirmed,
                    RefundedAmount = fullyRefunded ? data.Attributes.Total / 100m : 0,
                    BuyerEmail = data.Attributes.UserEmail,
                    BuyerName = data.Attributes.UserName,
                    Locale = subUser?.Locale,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    IsSubscriptionPayment = true
                });
            }
            else if (meta.EventName is "subscription_updated" or "subscription_plan_changed" or "subscription_created" or "subscription_cancelled"
                or "subscription_resumed" or "subscription_expired" or "subscription_paused" or "subscription_unpaused")
                await subscriptionService.UpdateSubscription(webhook);
            else if (meta.EventName == "subscription_payment_refunded")
            {
                await subscriptionService.RefundPayment(webhook);
                await MarkPaymentRefunded(data.Attributes.Identifier ?? "ls-invoice-" + data.Id, "lemonsqueezy",
                    data.Attributes.RefundedAmount / 100m, data.Attributes.Refunded || data.Attributes.Status == "refunded"
                        || (data.Attributes.Total > 0 && data.Attributes.RefundedAmount >= data.Attributes.Total));
            }
            else if (meta.EventName == "subscription_payment_failed")
            {
                _logger.LogInformation("Subscription payment failed for {userId} {productId}", meta.CustomData?.UserId, meta.CustomData?.ProductId);
            }
            else
            {
                _logger.LogWarning($"lemonsqueezy unknown type {meta.EventName}");
                return StatusCode(500);
            }
            return Ok();
        }

        /// <summary>
        /// Webhook callback for CoinGate cryptocurrency payments
        /// </summary>
        /// <param name="userId">User ID from callback URL</param>
        /// <param name="productId">Product ID from callback URL</param>
        /// <param name="coinAmount">Coin amount from callback URL</param>
        /// <returns>Status result</returns>
        [HttpPost]
        [Route("coingate")]
        public async Task<IActionResult> CoinGate(
            [FromQuery] string userId, 
            [FromQuery] int productId, 
            [FromQuery] decimal coinAmount)
        {
            _logger.LogInformation("Received callback from CoinGate for user {UserId}, product {ProductId}", userId, productId);

            var syncIOFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }

            string json = "";
            try
            {
                json = await new StreamReader(Request.Body).ReadToEndAsync();
                _logger.LogInformation("CoinGate callback body: {Body}", json);

                if (string.IsNullOrEmpty(json))
                {
                    _logger.LogWarning("CoinGate callback received empty body");
                    return BadRequest("Empty request body");
                }

                // Parse the callback data
                CoinGateCallback callback;
                
                // CoinGate can send data as form-encoded or JSON depending on API App configuration
                if (Request.ContentType?.Contains("application/json") == true)
                {
                    callback = System.Text.Json.JsonSerializer.Deserialize<CoinGateCallback>(json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                else
                {
                    // Parse form-encoded data
                    var formData = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(json);
                    callback = new CoinGateCallback
                    {
                        Id = long.Parse(formData["id"].ToString()),
                        OrderId = formData["order_id"].ToString(),
                        Status = formData["status"].ToString(),
                        PriceAmount = decimal.Parse(formData["price_amount"].ToString()),
                        PriceCurrency = formData["price_currency"].ToString(),
                        ReceiveCurrency = formData.ContainsKey("receive_currency") ? formData["receive_currency"].ToString() : null,
                        ReceiveAmount = formData.ContainsKey("receive_amount") && decimal.TryParse(formData["receive_amount"].ToString(), out var ra) ? ra : null,
                        PayAmount = formData.ContainsKey("pay_amount") && decimal.TryParse(formData["pay_amount"].ToString(), out var pa) ? pa : null,
                        PayCurrency = formData.ContainsKey("pay_currency") ? formData["pay_currency"].ToString() : null,
                        Token = formData.ContainsKey("token") ? formData["token"].ToString() : null,
                        CreatedAt = DateTime.TryParse(formData["created_at"].ToString(), out var ca) ? ca : DateTime.UtcNow
                    };
                }

                _logger.LogInformation("CoinGate callback parsed: OrderId={OrderId}, Status={Status}, Amount={Amount} {Currency}", 
                    callback.OrderId, callback.Status, callback.PriceAmount, callback.PriceCurrency);

                // Verify the callback by fetching the order from CoinGate API
                var isValid = await _coinGateService.VerifyCallback(callback, userId, productId, coinAmount);
                if (!isValid)
                {
                    _logger.LogWarning("CoinGate callback verification failed for order {OrderId}", callback.OrderId);
                    return StatusCode(400, "Callback verification failed");
                }

                var user = await db.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
                var userCountry = user?.Country;

                // Process based on status
                switch (callback.Status)
                {
                    case CoinGateOrderStatus.Paid:
                        _logger.LogInformation("CoinGate payment completed for order {OrderId}, user {UserId}", callback.OrderId, userId);
                        
                        try
                        {
                            // Use the CoinGate order ID as the transaction reference
                            var reference = $"coingate:{callback.Id}";
                            await transactionService.AddTopUp(productId, userId, reference, (int)coinAmount);

                            // Send payment event
                            await paymentEventProducer.ProduceEvent(new PaymentEvent
                            {
                                PayedAmount = (double)callback.PriceAmount,
                                ProductId = productId.ToString(),
                                UserId = userId,
                                Currency = callback.PriceCurrency,
                                PaymentMethod = callback.PayCurrency ?? "crypto",
                                PaymentProvider = "coingate",
                                PaymentProviderTransactionId = callback.Id.ToString(),
                                Timestamp = callback.CreatedAt
                            });

                            _logger.LogInformation("CoinGate topup processed successfully: {OrderId}, {Amount} coins for user {UserId}", 
                                callback.OrderId, coinAmount, userId);

                            // Record for tax compliance — CoinGate does NOT remit tax
                            await RecordPayment(new PaymentRecord
                            {
                                UserId = user?.Id ?? 0,
                                ExternalUserId = userId,
                                Country = userCountry,
                                ZipCode = user?.Zip,
                                GrossAmount = callback.PriceAmount,
                                Subtotal = callback.PriceAmount,
                                TaxAmount = 0,
                                TaxRemittedByProcessor = false,
                                NetAmount = callback.ReceiveAmount ?? callback.PriceAmount,
                                ProcessorFee = callback.PriceAmount - (callback.ReceiveAmount ?? callback.PriceAmount),
                                Currency = callback.PriceCurrency?.ToUpper() ?? "USD",
                                Provider = "coingate",
                                PaymentMethod = callback.PayCurrency ?? "crypto",
                                ExternalOrderId = callback.OrderId,
                                ExternalTransactionId = callback.Id.ToString(),
                                ProductSlug = productId.ToString(),
                                ProductId = productId,
                                CoinAmount = (long)coinAmount,
                                PaidAt = callback.CreatedAt,
                                Status = PaymentRecordStatus.Confirmed,
                                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                            });
                        }
                        catch (TransactionService.DupplicateTransactionException)
                        {
                            _logger.LogInformation("CoinGate duplicate transaction for order {OrderId}", callback.OrderId);
                            // Already processed, return success
                        }
                        break;

                    case CoinGateOrderStatus.Confirming:
                    case CoinGateOrderStatus.Pending:
                        _logger.LogInformation("CoinGate payment pending/confirming for order {OrderId}", callback.OrderId);
                        // Payment is in progress, nothing to do yet
                        break;

                    case CoinGateOrderStatus.Expired:
                    case CoinGateOrderStatus.Canceled:
                        _logger.LogInformation("CoinGate payment {Status} for order {OrderId}", callback.Status, callback.OrderId);
                        // Payment was not completed
                        break;

                    case CoinGateOrderStatus.Invalid:
                        _logger.LogWarning("CoinGate payment invalid for order {OrderId}", callback.OrderId);
                        // Payment failed or was underpaid
                        break;

                    case CoinGateOrderStatus.Refunded:
                        _logger.LogInformation("CoinGate payment refunded for order {OrderId}, reverting transaction", callback.OrderId);
                        try
                        {
                            var reference = $"coingate:{callback.Id}";
                            await transactionService.ApplyTopUpRefund(reference, 1, 1, "coingate");
                            await MarkPaymentRefunded(callback.OrderId, "coingate");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to revert CoinGate transaction for order {OrderId}", callback.OrderId);
                        }
                        break;

                    default:
                        _logger.LogWarning("CoinGate unknown status {Status} for order {OrderId}", callback.Status, callback.OrderId);
                        break;
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CoinGate callback processing failed. Body: {Body}", json);
                return StatusCode(500);
            }
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
            await transactionService.WithTransactionAsync(async (tx, owns) =>
            {
                await RevertTopUpWithReference(topupSlug, webhook.Meta.CustomData.UserId);
                await RevertTopUpWithReference(purchase.Reference, webhook.Meta.CustomData.UserId, true);
            });
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
                var webhook = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (!await VerifyPayPalWebhook(json))
                    return Unauthorized();
                var webhookResult = webhook.ToObject<PayPalWebhookData>();
                if (string.IsNullOrWhiteSpace(webhookResult.Id) || !ValidPayPalId(webhookResult.Resource?.Id))
                    return BadRequest();
                _logger.LogInformation(Newtonsoft.Json.JsonConvert.SerializeObject(webhookResult));
                if (webhookResult.EventType == "CHECKOUT.ORDER.APPROVED")
                {
                    var approved = (await paypalClient.Execute(new OrdersGetRequest(webhookResult.Resource.Id))).Result<Order>();
                    if (approved.Id != webhookResult.Resource.Id)
                        return BadRequest();
                    if (approved.Status == "COMPLETED")
                        return Ok();
                    if (approved.Status != "APPROVED")
                        return BadRequest();
                    webhookResult.Resource = approved;
                    var payerAddress = approved.Payer?.AddressPortable;
                    var country = payerAddress?.CountryCode?.ToUpperInvariant();
                    var postalCode = payerAddress?.PostalCode;
                    var state = payerAddress?.AdminArea1;
                    var userId = webhookResult.Resource.PurchaseUnits[0].CustomId.Split(';')[2];
                    var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
                    if (user != null && user.Country != country)
                    {
                        user.Country = country;
                        await db.SaveChangesAsync();
                    }
                    if (country == null || !DoWeSellto(country, postalCode))
                    {
                        _logger.LogWarning(
                            "PayPal payment rejected before capture: payer country {Country} is unavailable or not accepted",
                            country ?? "UNKNOWN");
                        return Ok();
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
                    referenceId = webhookResult.Resource.Id;
                }
                else if (webhookResult.EventType == "PAYMENT.CAPTURE.REFUNDED")
                {
                    // Only PayPal's authenticated API response may identify the original capture.
                    var refund = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.RefundsGetRequest(webhookResult.Resource.Id)))
                        .Result<PayPalCheckoutSdk.Payments.Refund>();
                    if (refund.Id != webhookResult.Resource.Id || refund.Status != "COMPLETED")
                        return BadRequest();
                    var captureId = PayPalCaptureId(refund.Links?.SingleOrDefault(l => l.Rel == "up")?.Href);
                    var capture = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.CapturesGetRequest(captureId)))
                        .Result<PayPalCheckoutSdk.Payments.Capture>();
                    if (capture.Id != captureId || capture.Status is not ("REFUNDED" or "PARTIALLY_REFUNDED"))
                        return BadRequest();
                    var total = decimal.Parse(capture.Amount.Value, CultureInfo.InvariantCulture);
                    var refunded = refund.SellerPayableBreakdown?.TotalRefundedAmount;
                    if (refund.Amount?.CurrencyCode != capture.Amount.CurrencyCode
                        || (capture.Status != "REFUNDED" && refunded?.CurrencyCode != capture.Amount.CurrencyCode))
                        return BadRequest();
                    var refundedAmount = capture.Status == "REFUNDED"
                        ? total : decimal.Parse(refunded.Value, CultureInfo.InvariantCulture);
                    if (refundedAmount <= 0 || refundedAmount > total)
                        return BadRequest();
                    var deduction = await transactionService.ApplyTopUpRefund(captureId, total, refundedAmount, "paypal");
                    if (!deduction.HasValue)
                        return BadRequest(); // Retry if the original credit has not arrived yet.
                    await MarkPaymentRefunded(captureId, "paypal", refundedAmount, refundedAmount == total);
                    return Ok();
                }
                else
                {
                    return Ok();
                }

                var verifiedCapture = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.CapturesGetRequest(referenceId)))
                    .Result<PayPalCheckoutSdk.Payments.Capture>();
                if (verifiedCapture.Id != referenceId)
                    return BadRequest();
                // A delayed completion must never grant coins after the capture was refunded.
                if (verifiedCapture.Status is "REFUNDED" or "PARTIALLY_REFUNDED")
                    return Ok();
                if (verifiedCapture.Status != "COMPLETED")
                    return BadRequest();
                var orderId = webhook.SelectToken("resource.supplementary_data.related_ids.order_id")?.Value<string>();
                if (!ValidPayPalId(orderId))
                    return BadRequest();
                var order = (await paypalClient.Execute(new OrdersGetRequest(orderId))).Result<Order>();
                if (order.Id != orderId || order.PurchaseUnits?.Count != 1
                    || order.PurchaseUnits[0].Payments?.Captures?.SingleOrDefault()?.Id != referenceId
                    || order.PurchaseUnits[0].Payments.Captures[0].Status != "COMPLETED"
                    || order.PurchaseUnits[0].AmountWithBreakdown.CurrencyCode != verifiedCapture.Amount.CurrencyCode
                    || decimal.Parse(order.PurchaseUnits[0].AmountWithBreakdown.Value, CultureInfo.InvariantCulture)
                        != decimal.Parse(verifiedCapture.Amount.Value, CultureInfo.InvariantCulture))
                    return BadRequest();
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

                var transactionId = order.Id;
                var product = order.PurchaseUnits[0];
                var topupInfo = product.CustomId.Split(';');
                _logger.LogInformation($"user {product.ReferenceId} purchased '{product.CustomId}' via PayPal {transactionId}");
                var exactCoinAmount = 0;
                if (topupInfo.Length >= 2)
                    int.TryParse(topupInfo[1], out exactCoinAmount);
                var productId = int.Parse(topupInfo[0]);
                if (!await db.TopUpProducts.AnyAsync(p => p.Id == productId && p.ProviderSlug == "paypal")
                    || topupInfo.Length != 3 || topupInfo[2] != product.ReferenceId)
                    return BadRequest();
                var alreadyCredited = false;
                await transactionService.WithTransactionAsync(async (tx, owns) =>
                {
                    var existing = await db.FiniteTransactions.SingleOrDefaultAsync(t =>
                        (t.Reference == referenceId || t.Reference == order.Id)
                        && t.ProductId == productId && t.User.ExternalId == product.ReferenceId && t.Amount > 0);
                    if (existing != null)
                    {
                        existing.Reference = referenceId;
                        await db.SaveChangesAsync();
                        alreadyCredited = true;
                        return;
                    }
                    await transactionService.AddTopUp(productId, product.ReferenceId, referenceId, exactCoinAmount);
                });
                if (alreadyCredited)
                    return Ok();

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
                        CountryCode = order.Payer?.AddressPortable?.CountryCode,
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

                // Record for tax compliance — PayPal does NOT remit tax for us
                var ppUser = await db.Users.Where(u => u.ExternalId == product.ReferenceId).FirstOrDefaultAsync();
                var payerRecordAddress = order.Payer?.AddressPortable;
                await RecordPayment(new PaymentRecord
                {
                    UserId = ppUser?.Id ?? 0,
                    ExternalUserId = product.ReferenceId,
                    Country = payerRecordAddress?.CountryCode,
                    ZipCode = payerRecordAddress?.PostalCode,
                    City = payerRecordAddress?.AdminArea2,
                    State = payerRecordAddress?.AdminArea1,
                    GrossAmount = decimal.Parse(amount.Value),
                    Subtotal = decimal.Parse(amount.Value),
                    TaxAmount = 0,
                    TaxRemittedByProcessor = false,
                    ProcessorFee = 0, // PayPal fee not available in webhook
                    Currency = amount.CurrencyCode?.ToUpper() ?? "USD",
                    Provider = "paypal",
                    PaymentMethod = "paypal",
                    ExternalOrderId = referenceId,
                    ExternalTransactionId = transactionId,
                    ProductSlug = topupInfo[0],
                    ProductId = int.TryParse(topupInfo[0], out var ppProdId) ? ppProdId : null,
                    CoinAmount = exactCoinAmount,
                    PaidAt = string.IsNullOrEmpty(order.CreateTime) ? DateTime.UtcNow : DateTime.Parse(order.CreateTime),
                    Status = PaymentRecordStatus.Confirmed,
                    BuyerEmail = order.Payer?.Email,
                    BuyerName = product.ShippingDetail?.Name?.FullName,
                    IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
                });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "paypal checkout");
                return StatusCode(400);
            }

            return Ok();
        }

        private async Task<bool> VerifyPayPalWebhook(string json)
        {
            if (string.IsNullOrWhiteSpace(paypalWebhookId))
                throw new InvalidOperationException("PAYPAL:WEBHOOK_ID is required to accept PayPal webhooks");
            var body = new JObject { ["webhook_id"] = paypalWebhookId, ["webhook_event"] = new JRaw(json) };
            foreach (var field in new[] { "auth_algo", "cert_url", "transmission_id", "transmission_sig", "transmission_time" })
            {
                var header = Request.Headers["PAYPAL-" + field.Replace('_', '-')];
                if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
                    return false;
                body[field] = header[0];
            }
            // Post the entire event to PayPal; never fetch a caller-supplied certificate URL locally.
            using var request = new PayPalHttp.HttpRequest("/v1/notifications/verify-webhook-signature", HttpMethod.Post, typeof(PayPalVerification))
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            var response = await paypalClient.Execute(request);
            return response.Result<PayPalVerification>()?.Status == "SUCCESS";
        }

        private static bool ValidPayPalId(string id) => !string.IsNullOrWhiteSpace(id)
            && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        private static string PayPalCaptureId(string apiLink)
        {
            const string prefix = "/v2/payments/captures/";
            if (!Uri.TryCreate(apiLink, UriKind.Absolute, out var uri)
                || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
                || !ValidPayPalId(uri.AbsolutePath[prefix.Length..]))
                throw new ApiException("PayPal refund has no capture reference");
            // Only the ID is used in a fixed SDK request to the configured PayPal environment.
            return uri.AbsolutePath[prefix.Length..];
        }

        [DataContract]
        public class PayPalVerification
        {
            [DataMember(Name = "verification_status")]
            public string Status { get; set; }
        }

        private async Task<FiniteTransaction> RevertTopUpWithReference(string id, string userId, bool revertTime = false)
        {
            var transaction = await db.FiniteTransactions.Where(t => t.Reference == id && t.User.ExternalId == userId)
                .Include(t => t.User).Include(t => t.Product).SingleOrDefaultAsync();
            if (transaction == null || (!revertTime && (transaction.Amount <= 0 || !transaction.Product.Type.HasFlag(Coflnet.Payments.Models.Product.ProductType.TOP_UP))))
                throw new ApiException("No positive top-up found for refund");
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

        /// <summary>
        /// Validates if we accept CoinGate cryptocurrency payments from a specific country.
        /// Rules:
        /// - EU countries are always accepted
        /// - US is accepted only for amounts >= 3000 CoflCoins
        /// - VAT-free countries are accepted
        /// </summary>
        /// <param name="country">ISO 3166-1 alpha-2 country code</param>
        /// <param name="coinAmount">Amount of CoflCoins being purchased</param>
        /// <returns>True if we accept CoinGate payment from this country</returns>
        public static bool DoWeAcceptCoinGateFrom(string country, decimal coinAmount)
        {
            // EU member states (27 countries as of 2024)
            var euCountries = new string[] 
            { 
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", 
                "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", 
                "SI", "ES", "SE"
            };

            // Extra VAT-free countries
            var vatFreCountries = new string[]
            {
                "BM", // Bermuda
                "GI", // Gibraltar
                "GG", // Guernsey
                "GL", // Greenland
                "HK", // Hong Kong
                "KI", // Kiribati
                "MO", // Macau
                "MV", // Maldives
                "GS", // South Georgia and South Sandwich Islands
                "SJ", // Svalbard and Jan Mayen
                "JE", // Jersey
            };

            // Check if country is in EU
            if (euCountries.Contains(country))
                return true;

            // Check US - only if amount >= 3000 CoflCoins
            if (country == "US")
                return coinAmount >= 3000;

            // Check VAT-free countries
            if (vatFreCountries.Contains(country))
                return true;

            return false;
        }

        private static string GetStripePaymentCountry(Stripe.PaymentMethod paymentMethod)
        {
            var country = paymentMethod?.Type switch
            {
                "card" => paymentMethod.Card?.Country,
                "paypal" => paymentMethod.Paypal?.Country,
                "sepa_debit" => paymentMethod.SepaDebit?.Country,
                "sofort" => paymentMethod.Sofort?.Country,
                "au_becs_debit" => "AU",
                "bacs_debit" => "GB",
                "bancontact" => "BE",
                "eps" => "AT",
                "fpx" => "MY",
                "ideal" => "NL",
                "us_bank_account" => "US",
                _ => null
            };
            return country?.ToUpperInvariant();
        }

        /// <summary>
        /// Validates if we accept traditional payments (PayPal, etc.) from a specific country
        /// </summary>
        /// <param name="country">ISO 3166-1 alpha-2 country code</param>
        /// <param name="postalCode">Postal code</param>
        /// <returns>True if we accept payment from this country</returns>
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

                // Record for tax compliance — Google Play IS Merchant of Record and remits tax
                var gpUser = await db.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
                await RecordPayment(new PaymentRecord
                {
                    UserId = gpUser?.Id ?? 0,
                    ExternalUserId = userId,
                    Country = gpUser?.Country,
                    ZipCode = gpUser?.Zip,
                    GrossAmount = product.Price,
                    Subtotal = product.Price,
                    TaxAmount = 0, // Google doesn't break out tax in purchase object
                    TaxRemittedByProcessor = true, // Google handles tax
                    ProcessorFee = 0, // Google takes 15-30% but not exposed per-transaction
                    Currency = product.CurrencyCode?.ToUpper() ?? "USD",
                    Provider = "googlepay",
                    PaymentMethod = "googlepay",
                    ExternalOrderId = purchase.OrderId,
                    ProductSlug = sku,
                    ProductId = product.Id,
                    CoinAmount = customAmount,
                    PaidAt = DateTime.UtcNow,
                    Status = PaymentRecordStatus.Confirmed,
                    Locale = gpUser?.Locale
                });
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

                // Record for tax compliance — Google Play is MoR
                var gpSubUser = await db.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
                await RecordPayment(new PaymentRecord
                {
                    UserId = gpSubUser?.Id ?? 0,
                    ExternalUserId = userId,
                    Country = gpSubUser?.Country ?? subscription.CountryCode,
                    ZipCode = gpSubUser?.Zip,
                    GrossAmount = product.Price,
                    Subtotal = product.Price,
                    TaxAmount = 0,
                    TaxRemittedByProcessor = true,
                    Currency = product.CurrencyCode?.ToUpper() ?? "USD",
                    Provider = "googlepay",
                    PaymentMethod = "googlepay",
                    ExternalOrderId = orderId,
                    ProductSlug = subscriptionId,
                    ProductId = product.Id,
                    CoinAmount = customAmount,
                    PaidAt = DateTime.UtcNow,
                    Status = PaymentRecordStatus.Confirmed,
                    Locale = gpSubUser?.Locale,
                    IsSubscriptionPayment = true
                });
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
        /// Records a payment in the PaymentRecords table for tax compliance.
        /// Call this from every callback after a payment is confirmed/captured.
        /// </summary>
        private async Task RecordPayment(PaymentRecord record)
        {
            try
            {
                record.RecordedAt = DateTime.UtcNow;
                if (record.PaidAt == default)
                    record.PaidAt = DateTime.UtcNow;
                
                // Ensure all DateTimes are UTC (PostgreSQL timestamp with time zone requires UTC)
                if (record.PaidAt.Kind != DateTimeKind.Utc)
                    record.PaidAt = record.PaidAt.ToUniversalTime();
                if (record.RefundedAt.HasValue && record.RefundedAt.Value.Kind != DateTimeKind.Utc)
                    record.RefundedAt = record.RefundedAt.Value.ToUniversalTime();
                
                if (record.NetAmount == 0 && record.GrossAmount > 0)
                    record.NetAmount = record.GrossAmount - record.TaxAmount - record.ProcessorFee;

                db.PaymentRecords.Add(record);
                await db.SaveChangesAsync();
                _logger.LogInformation("PaymentRecord created: {Provider} {ExternalOrderId} {GrossAmount} {Currency} for user {UserId}",
                    record.Provider, record.ExternalOrderId, record.GrossAmount, record.Currency, record.ExternalUserId);
            }
            catch (Exception ex)
            {
                // Never let a recording failure break the actual payment flow
                _logger.LogError(ex, "Failed to record PaymentRecord for {Provider} order {ExternalOrderId}", record.Provider, record.ExternalOrderId);
            }
        }

        /// <summary>
        /// Marks a payment record as refunded by external order ID
        /// </summary>
        private async Task MarkPaymentRefunded(string externalOrderId, string provider, decimal? refundedAmount = null, bool isFullRefund = true)
        {
            try
            {
                var record = await db.PaymentRecords
                    .Where(r => r.Provider == provider && (r.ExternalOrderId == externalOrderId || r.ExternalTransactionId == externalOrderId))
                    .FirstOrDefaultAsync();
                if (record != null)
                {
                    if (refundedAmount.HasValue)
                        record.RefundedAmount = Math.Max(record.RefundedAmount, refundedAmount.Value);
                    else if (isFullRefund)
                        record.RefundedAmount = record.GrossAmount;

                    var fullyRefunded = isFullRefund
                        || record.Status == PaymentRecordStatus.Refunded
                        || (record.GrossAmount > 0 && record.RefundedAmount >= record.GrossAmount);
                    record.Status = fullyRefunded
                        ? PaymentRecordStatus.Refunded
                        : PaymentRecordStatus.PartiallyRefunded;
                    record.RefundedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    _logger.LogInformation(
                        "PaymentRecord refund updated: {ExternalOrderId}, status={Status}, refunded={RefundedAmount}",
                        externalOrderId,
                        record.Status,
                        record.RefundedAmount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark PaymentRecord refunded for {ExternalOrderId}", externalOrderId);
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
