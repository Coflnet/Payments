using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
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

        public CallbackController(
            IConfiguration config,
            ILogger<CallbackController> logger,
            PaymentContext context,
            TransactionService transactionService,
            PayPalHttpClient paypalClient,
            IPaymentEventProducer paymentEventProducer,
            Coflnet.Payments.Services.SubscriptionService subscriptionService)
        {
            _logger = logger;
            db = context;
            signingSecret = config["STRIPE:SIGNING_SECRET"];
            lemonSqueezySecret = config["LEMONSQUEEZY:SECRET"] ?? throw new Exception("Lemon Squeezy Secret not set");
            this.transactionService = transactionService;
            this.paypalClient = paypalClient;
            this.paymentEventProducer = paymentEventProducer;
            this.subscriptionService = subscriptionService;
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
                var webhookResult = Newtonsoft.Json.JsonConvert.DeserializeObject<PayPalWebhook>(json);
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

        private async Task<FiniteTransaction> RevertTopUpWithReference(string id)
        {
            var transaction = await db.FiniteTransactions.Where(t => t.Reference == id).Include(t => t.User).FirstOrDefaultAsync();
            await transactionService.RevertPurchase(transaction.User.ExternalId, transaction.Id);
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

        [DataContract]
        public class PayPalWebhook
        {
            [DataMember(Name = "id")]
            public string Id { get; set; }
            [DataMember(Name = "create_time")]
            public DateTime CreateTime;
            [DataMember(Name = "event_type")]
            public string EventType;
            [DataMember(Name = "resource")]
            public PayPalCheckoutSdk.Orders.Order Resource;
        }
    }
}
