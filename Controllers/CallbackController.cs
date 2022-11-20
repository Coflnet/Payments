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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Serialization;
using PayPalCheckoutSdk.Core;

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
        private TransactionService transactionService;
        private readonly PayPalHttpClient paypalClient;

        public CallbackController(
            IConfiguration config,
            ILogger<CallbackController> logger,
            PaymentContext context,
            TransactionService transactionService,
            PayPalHttpClient paypalClient)
        {
            _logger = logger;
            db = context;
            signingSecret = config["STRIPE:SIGNING_SECRET"];
            this.transactionService = transactionService;
            this.paypalClient = paypalClient;
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
                        await transactionService.AddTopUp(productId, session.ClientReferenceId, session.Id, coinAmount);
                    }
                    catch (TransactionService.DupplicateTransactionException)
                    {
                        // this already happened so was successful
                    }
                }
                else
                {
                    _logger.LogWarning("sripe is not comlete type of " + stripeEvent.Type);
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
            try
            {
                _logger.LogInformation("reading json");
                json = await new StreamReader(Request.Body).ReadToEndAsync();
                var webhookResult = Newtonsoft.Json.JsonConvert.DeserializeObject<PayPalWebhook>(json);
                PayPalCheckoutSdk.Orders.Order order = null;
                if (webhookResult.EventType == "CHECKOUT.ORDER.APPROVED")
                {
                    var address = webhookResult.Resource.PurchaseUnits[0].ShippingDetail.AddressPortable;
                    var country = address.CountryCode;
                    var postalCode = address.PostalCode;
                    if(!DoWeSellto(country, postalCode))
                        return Ok(); // ignore order
                    // completing order
                    order = await CompleteOrder(paypalClient, webhookResult.Resource.Id);
                    _logger.LogInformation("completing order " + webhookResult.Resource.Id);
                }
                else if (webhookResult.EventType == "PAYMENT.CAPTURE.COMPLETED")
                {
                    dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
                    var id = (string)data.resource.supplementary_data.related_ids.order_id;
                    _logger.LogInformation("received confirmation for purchase " + id);

                    return Ok();
                }

                //3. Call PayPal to get the transaction
                PayPalHttp.HttpResponse response;
                _logger.LogInformation(Newtonsoft.Json.JsonConvert.SerializeObject(webhookResult));
                try
                {
                    OrdersGetRequest getRequest = new OrdersGetRequest(webhookResult.Resource.Id);
                    _logger.LogInformation("getting order " + webhookResult.Resource.Id);
                    response = paypalClient.Execute(getRequest).Result;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "payPalPayment");
                    throw new ApiException("The provided orderId has not vaid payment asociated");
                }
                //4. Save the transaction in your database. Implement logic to save transaction to your database for future reference.
                order = response.Result<PayPalCheckoutSdk.Orders.Order>();
                _logger.LogInformation("Retrieved Order Status");
                AmountWithBreakdown amount = order.PurchaseUnits[0].AmountWithBreakdown;
                _logger.LogInformation("Total Amount: {0} {1}", amount.CurrencyCode, amount.Value);

                if (order.Status != "COMPLETED")
                    throw new ApiException("The order is not yet completed");

                _logger.LogInformation("Status: {0}", order.Status);

                _logger.LogInformation("Order Id: {0}", order.Id);
                if (DateTime.Parse(order.PurchaseUnits[0].Payments.Captures[0].UpdateTime) < DateTime.UtcNow.Subtract(TimeSpan.FromHours(1)))
                    throw new Exception("the provied order id is too old, please contact support for manual review");

                var transactionId = order.Id;
                var product = order.PurchaseUnits[0];
                var topupInfo = product.CustomId.Split(';');
                _logger.LogInformation($"user {product.ReferenceId} purchased '{product.CustomId}' via PayPal {transactionId}");
                var exactCoinAmount = 0;
                if (topupInfo.Length >= 2)
                    int.TryParse(topupInfo[1], out exactCoinAmount);
                await transactionService.AddTopUp(int.Parse(topupInfo[0]), product.ReferenceId, order.Id, exactCoinAmount);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "paypal checkout");
                // return StatusCode(400);
            }

            return Ok();
        }

        private static bool DoWeSellto(string country, string postalCode)
        {
            if (country == "GB" && postalCode.StartsWith("BT"))
                return false; // registration too complicated for northern ireland
            return true;
        }

        private async Task<PayPalCheckoutSdk.Orders.Order> CompleteOrder(PayPalCheckoutSdk.Core.PayPalHttpClient client, string id)
        {
            var request = new OrdersCaptureRequest(id);
            request.RequestBody(new OrderActionRequest());
            HttpResponse responsea = await client.Execute(request);
            var statusCode = responsea.StatusCode;
            var result = responsea.Result<PayPalCheckoutSdk.Orders.Order>();
            _logger.LogInformation("Status: {0}", result.Status);
            _logger.LogInformation("Capture Id: {0}", result.Id);
            return result;
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
