using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;

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

        public CallbackController(
            IConfiguration config,
            ILogger<CallbackController> logger,
            PaymentContext context,
            TransactionService transactionService)
        {
            _logger = logger;
            db = context;
            signingSecret = config["STRIPE:SIGNING_SECRET"];
            this.transactionService = transactionService;
        }

        /// <summary>
        /// Webhook callback for stripe
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("stripe")]
        public async Task<IActionResult> CreateStripe(string userId)
        {

            _logger.LogInformation("received callback from stripe --");
            string json = "";
            try
            {
                _logger.LogInformation("reading json");
                json = new StreamReader(Request.Body).ReadToEnd();

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
                    await transactionService.AddTopUp(session.LineItems.Data[0].Price.ProductId, session.ClientReferenceId, session.Id);
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
    }
}
