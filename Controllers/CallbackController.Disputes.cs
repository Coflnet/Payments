using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Stripe;

namespace Payments.Controllers;

/// <summary>
/// Chargebacks (disputes) of Stripe and PayPal payments: a subscription payment is reverted like a full refund and the
/// subscription cancelled (<see cref="SubscriptionService.StripeInvoiceDisputed"/>), a one-time top-up loses its coins;
/// a won dispute gives both back once. Logs carry ids and amounts only.
/// </summary>
public partial class CallbackController
{
    /// <summary>
    /// charge.dispute.created and charge.dispute.funds_withdrawn both open the dispute (the second is the first's follow-up
    /// for chargebacks, and the only one that tells an inquiry has become one), charge.dispute.closed decides it.
    /// Returns false for events this does not handle.
    /// </summary>
    internal async Task<bool> HandleStripeDisputeEvent(Event stripeEvent)
    {
        if (stripeEvent.Data.Object is not Dispute dispute)
            return false;
        switch (stripeEvent.Type)
        {
            case EventTypes.ChargeDisputeCreated or EventTypes.ChargeDisputeFundsWithdrawn:
                await StripeDisputeOpened(dispute);
                return true;
            case EventTypes.ChargeDisputeClosed:
                await StripeDisputeClosed(dispute);
                return true;
            case EventTypes.ChargeDisputeUpdated or EventTypes.ChargeDisputeFundsReinstated:
                _logger.LogInformation("Stripe dispute {DisputeId} of payment {PaymentIntentId} is {Status}; nothing to do",
                    dispute.Id, dispute.PaymentIntentId, dispute.Status);
                return true;
            default:
                return false;
        }
    }

    private async Task StripeDisputeOpened(Dispute dispute)
    {
        if (dispute.Status?.StartsWith("warning_", StringComparison.Ordinal) == true)
        {
            _logger.LogInformation("Stripe dispute {DisputeId} of payment {PaymentIntentId} is only an inquiry ({Status}); nothing reverted",
                dispute.Id, dispute.PaymentIntentId, dispute.Status);
            return;
        }
        if (string.IsNullOrEmpty(dispute.PaymentIntentId))
        {
            _logger.LogWarning("Stripe dispute {DisputeId} has no payment intent; ignored", dispute.Id);
            return;
        }
        // Without Stripe's answer (a dispute of another account) the event's own status decides
        var live = await subscriptionService.StripeDisputeStatus(dispute.Id) ?? dispute.Status;
        if (live is "won" or "warning_closed" or "prevented")
        {
            _logger.LogInformation("Stripe dispute {DisputeId} of payment {PaymentIntentId} is already {Status}; a late opening event is ignored",
                dispute.Id, dispute.PaymentIntentId, live);
            return;
        }
        var invoice = await subscriptionService.StripeSubscriptionInvoiceForPayment(dispute.PaymentIntentId);
        if (invoice != null)
        {
            await subscriptionService.StripeInvoiceDisputed(invoice);
            await ReverseCreatorRevenue(invoice.Id, "dispute", 1);
            _logger.LogWarning("Stripe dispute {DisputeId} ({Amount} {Currency}) handled for subscription invoice {InvoiceId}",
                dispute.Id, dispute.Amount, dispute.Currency, invoice.Id);
            return;
        }
        await TopUpDisputed(dispute.PaymentIntentId, "stripe", dispute.Id, dispute.Amount.ToString(), dispute.Currency, 1, 1);
    }

    private async Task TopUpDisputed(string paymentId, string provider, string disputeId, string amount, string currency,
        decimal totalAmount, decimal reversedAmount)
    {
        var deducted = await transactionService.ApplyTopUpDispute(paymentId, provider, totalAmount, reversedAmount);
        if (deducted == null)
        {
            _logger.LogWarning("{Provider} dispute {DisputeId} of payment {PaymentId} matches no payment of ours; ignored", provider, disputeId, paymentId);
            return;
        }
        PaymentRecordDispute.Open(await PaymentRecordDispute.Find(db, provider, paymentId));
        await db.SaveChangesAsync();
        if (deducted is not > 0)
        {
            _logger.LogInformation("{Provider} dispute {DisputeId} of top-up {PaymentId}: nothing left to deduct (refunded or already handled)", provider, disputeId, paymentId);
            return;
        }
        PaymentMetrics.Disputes.WithLabels(provider, "opened").Inc();
        _logger.LogWarning("{Provider} dispute {DisputeId} ({Amount} {Currency}) took {Coins} coins of top-up {PaymentId} back",
            provider, disputeId, amount, currency, deducted, paymentId);
    }

    private async Task StripeDisputeClosed(Dispute dispute)
    {
        switch (dispute.Status)
        {
            case "won":
                await StripeDisputeWon(dispute);
                return;
            case "lost":
                PaymentMetrics.Disputes.WithLabels("stripe", "lost").Inc();
                _logger.LogWarning("Stripe dispute {DisputeId} of payment {PaymentIntentId} was lost ({Amount} {Currency}); stays reverted",
                    dispute.Id, dispute.PaymentIntentId, dispute.Amount, dispute.Currency);
                return;
            default:
                _logger.LogInformation("Stripe dispute {DisputeId} of payment {PaymentIntentId} closed as {Status}; no revert or restore applies",
                    dispute.Id, dispute.PaymentIntentId, dispute.Status);
                return;
        }
    }

    private async Task StripeDisputeWon(Dispute dispute)
    {
        if (string.IsNullOrEmpty(dispute.PaymentIntentId))
            return;
        var invoice = await subscriptionService.StripeSubscriptionInvoiceForPayment(dispute.PaymentIntentId);
        if (invoice != null)
        {
            await subscriptionService.StripeInvoiceDisputeWon(invoice);
            return;
        }
        await TopUpDisputeWon(dispute.PaymentIntentId, "stripe", dispute.Id);
    }

    private async Task TopUpDisputeWon(string paymentId, string provider, string disputeId)
    {
        var restored = await transactionService.RestoreDisputedTopUp(paymentId, provider);
        PaymentRecordDispute.Won(await PaymentRecordDispute.Find(db, provider, paymentId));
        await db.SaveChangesAsync();
        if (restored is not > 0)
        {
            _logger.LogInformation("{Provider} dispute {DisputeId} won: nothing to restore for top-up {PaymentId} (not deducted, refunded or restored already)", provider, disputeId, paymentId);
            return;
        }
        PaymentMetrics.Disputes.WithLabels(provider, "won").Inc();
        _logger.LogInformation("{Provider} dispute {DisputeId} won: {Coins} coins of top-up {PaymentId} restored", provider, disputeId, restored, paymentId);
    }

    private static bool IsPayPalDisputeEvent(string eventType) =>
        eventType != null && (eventType.StartsWith("CUSTOMER.DISPUTE.", StringComparison.Ordinal) || eventType == "PAYMENT.CAPTURE.REVERSED");

    /// <summary>
    /// CUSTOMER.DISPUTE.CREATED/UPDATED open the dispute once it is a chargeback (an inquiry or an alert only logs, and UPDATED
    /// catches the escalation), CUSTOMER.DISPUTE.RESOLVED decides it, PAYMENT.CAPTURE.REVERSED is a fund reversal of a top-up.
    /// The dispute's state is read from PayPal's API; the disputed payment is its seller transaction id (a subscription sale
    /// or a top-up capture). Unknown disputes and payments are answered with 200 so PayPal does not retry them.
    /// </summary>
    private async Task<IActionResult> HandlePayPalDisputeEvent(string eventType, JObject webhook)
    {
        var resource = webhook["resource"] as JObject;
        if (eventType == "PAYMENT.CAPTURE.REVERSED")
            return await PayPalCaptureReversed(resource?.Value<string>("id"));
        var disputeId = resource?.Value<string>("dispute_id");
        if (!ValidPayPalId(disputeId))
            return BadRequest();
        var dispute = await subscriptionService.PayPalDispute(disputeId);
        var paymentId = dispute?.DisputedTransactions?.FirstOrDefault()?.SellerTransactionId;
        if (!ValidPayPalId(paymentId))
        {
            _logger.LogWarning("PayPal dispute {DisputeId} is unknown to PayPal or names no transaction; ignored", disputeId);
            return Ok();
        }
        switch (eventType)
        {
            case "CUSTOMER.DISPUTE.CREATED" or "CUSTOMER.DISPUTE.UPDATED":
                await PayPalDisputeChanged(dispute, paymentId);
                return Ok();
            case "CUSTOMER.DISPUTE.RESOLVED" when dispute.Status != "RESOLVED":
                _logger.LogWarning("PayPal dispute {DisputeId} is {Status} at PayPal although its resolution was announced; retry", disputeId, dispute.Status);
                return BadRequest();
            case "CUSTOMER.DISPUTE.RESOLVED":
                await PayPalDisputeResolved(dispute, paymentId);
                return Ok();
            default:
                _logger.LogInformation("PayPal dispute {DisputeId} event {EventType}; nothing to do", disputeId, eventType);
                return Ok();
        }
    }

    private static readonly string[] PayPalSellerWins = ["RESOLVED_SELLER_FAVOUR", "CANCELED_BY_BUYER", "DENIED"];

    private static bool IsPayPalSellerWin(PayPalDispute dispute) =>
        dispute.Status == "RESOLVED" && PayPalSellerWins.Contains(dispute.Outcome?.Code);

    /// <summary>A chargeback filed with the card issuer or escalated past the inquiry; PayPal alerts are only warnings</summary>
    private static bool IsPayPalChargeback(PayPalDispute dispute)
    {
        if (dispute.Channel == "ALERT")
            return false;
        return dispute.Channel == "EXTERNAL" || dispute.Stage is "CHARGEBACK" or "PRE_ARBITRATION" or "ARBITRATION";
    }

    private async Task PayPalDisputeChanged(PayPalDispute dispute, string paymentId)
    {
        if (IsPayPalSellerWin(dispute))
        {
            _logger.LogInformation("PayPal dispute {DisputeId} is already decided for the seller; a late opening event is ignored", dispute.Id);
            return;
        }
        if (!IsPayPalChargeback(dispute))
        {
            _logger.LogInformation("PayPal dispute {DisputeId} of payment {PaymentId} is only an inquiry or alert ({Channel}/{Stage}); nothing reverted",
                dispute.Id, paymentId, dispute.Channel, dispute.Stage);
            return;
        }
        await PayPalDisputeOpened(paymentId, dispute.Id, dispute.Amount?.Value, dispute.Amount?.CurrencyCode, 1, 1);
    }

    /// <summary>The reversal's capture is taken from PayPal's API like a refund's, never from the webhook body</summary>
    private async Task<IActionResult> PayPalCaptureReversed(string reversalId)
    {
        if (!ValidPayPalId(reversalId))
            return BadRequest();
        var reversal = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.RefundsGetRequest(reversalId)))
            .Result<PayPalCheckoutSdk.Payments.Refund>();
        if (reversal.Id != reversalId || reversal.Status != "COMPLETED")
            return BadRequest(); // a pending reversal is retried
        var captureId = PayPalCaptureId(reversal.Links?.SingleOrDefault(l => l.Rel == "up")?.Href);
        var capture = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.CapturesGetRequest(captureId)))
            .Result<PayPalCheckoutSdk.Payments.Capture>();
        if (capture.Id != captureId || (reversal.Amount != null && reversal.Amount.CurrencyCode != capture.Amount?.CurrencyCode))
            return BadRequest();
        var total = ParseMoney(capture.Amount?.Value);
        var reversed = reversal.Amount == null ? total : ParseMoney(reversal.Amount.Value);
        if (total is not > 0 || reversed is not > 0)
            return BadRequest();
        await PayPalDisputeOpened(captureId, reversalId, reversal.Amount?.Value ?? capture.Amount.Value, capture.Amount.CurrencyCode, total.Value, reversed.Value);
        return Ok();
    }

    private static decimal? ParseMoney(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private async Task PayPalDisputeOpened(string paymentId, string disputeId, string amount, string currency,
        decimal totalAmount, decimal reversedAmount)
    {
        if (await IsPayPalTopUp(paymentId))
        {
            await TopUpDisputed(paymentId, "paypal", disputeId, amount, currency, totalAmount, reversedAmount);
            return;
        }
        if (!await subscriptionService.PayPalSaleDisputed(paymentId))
        {
            _logger.LogWarning("PayPal dispute {DisputeId} of payment {PaymentId} matches no payment of ours; ignored", disputeId, paymentId);
            return;
        }
        await ReverseCreatorRevenue(paymentId, "dispute", 1);
        _logger.LogWarning("PayPal dispute {DisputeId} ({Amount} {Currency}) handled for subscription sale {SaleId}", disputeId, amount, currency, paymentId);
    }

    private async Task PayPalDisputeResolved(PayPalDispute dispute, string paymentId)
    {
        var outcome = dispute.Outcome?.Code;
        if (outcome is null or "NONE")
        {
            _logger.LogInformation("PayPal dispute {DisputeId} resolved without an outcome; nothing to do", dispute.Id);
            return;
        }
        if (IsPayPalSellerWin(dispute) || (outcome == "RESOLVED_WITH_PAYOUT" && await IsPayPalPaymentKept(paymentId)))
        {
            await PayPalDisputeWon(paymentId, dispute.Id);
            return;
        }
        if (!await PayPalPaymentWasReverted(paymentId))
        {
            _logger.LogInformation("PayPal dispute {DisputeId} of payment {PaymentId} resolved as {Outcome}; nothing had been reverted for it", dispute.Id, paymentId, outcome);
            return;
        }
        PaymentMetrics.Disputes.WithLabels("paypal", "lost").Inc();
        _logger.LogWarning("PayPal dispute {DisputeId} of payment {PaymentId} resolved as {Outcome}; stays reverted", dispute.Id, paymentId, outcome);
    }

    private async Task<bool> PayPalPaymentWasReverted(string paymentId) => await IsPayPalTopUp(paymentId)
        ? await transactionService.WasTopUpDisputed(paymentId, "paypal")
        : await subscriptionService.PayPalSaleWasDisputed(paymentId);

    private async Task PayPalDisputeWon(string paymentId, string disputeId)
    {
        if (await IsPayPalTopUp(paymentId))
            await TopUpDisputeWon(paymentId, "paypal", disputeId);
        else
            await subscriptionService.PayPalSaleDisputeWon(paymentId);
    }

    /// <summary>RESOLVED_WITH_PAYOUT: the seller keeps the payment only if PayPal did not take it back (sale completed / capture completed)</summary>
    private async Task<bool> IsPayPalPaymentKept(string paymentId)
    {
        if (!await IsPayPalTopUp(paymentId))
            return await subscriptionService.PayPalSaleKept(paymentId);
        var capture = (await paypalClient.Execute(new PayPalCheckoutSdk.Payments.CapturesGetRequest(paymentId)))
            .Result<PayPalCheckoutSdk.Payments.Capture>();
        return capture.Id == paymentId && capture.Status == "COMPLETED";
    }

    private Task<bool> IsPayPalTopUp(string paymentId) => db.FiniteTransactions.AnyAsync(t => t.Reference == paymentId);
}
