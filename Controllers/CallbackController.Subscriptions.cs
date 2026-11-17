using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Stripe;
using PaymentRecord = Coflnet.Payments.Models.PaymentRecord;

namespace Payments.Controllers;

/// <summary>
/// Webhooks of direct Stripe and PayPal subscriptions; one-time payments keep their own handlers.
/// </summary>
public partial class CallbackController
{
    /// <summary>
    /// Handles subscription events; returns false for events of one-time payments.
    /// </summary>
    private async Task<bool> HandleStripeSubscriptionEvent(Event stripeEvent)
    {
        switch (stripeEvent.Data.Object)
        {
            case Stripe.Checkout.Session { Mode: "subscription" } session
                when stripeEvent.Type is EventTypes.CheckoutSessionCompleted or EventTypes.CheckoutSessionAsyncPaymentSucceeded:
                await subscriptionService.StripeCheckoutCompleted(session);
                return true;
            case Invoice invoice when stripeEvent.Type == EventTypes.InvoicePaid:
                await RecordSubscriptionPayment(await subscriptionService.StripeInvoicePaid(StripeInvoiceInfo.From(invoice)), "stripe");
                return true;
            case Invoice invoice when stripeEvent.Type == EventTypes.InvoicePaymentFailed:
                var failed = StripeInvoiceInfo.From(invoice);
                if (failed.SubscriptionId != null)
                    await subscriptionService.StripeSubscriptionChanged(failed.SubscriptionId);
                return true;
            case Subscription subscription when stripeEvent.Type is EventTypes.CustomerSubscriptionUpdated
                or EventTypes.CustomerSubscriptionDeleted or EventTypes.CustomerSubscriptionCreated:
                await subscriptionService.StripeSubscriptionChanged(subscription.Id);
                return true;
            case Charge charge when stripeEvent.Type == EventTypes.ChargeRefunded:
                return await HandleStripeSubscriptionRefund(charge);
            default:
                return false;
        }
    }

    private async Task<bool> HandleStripeSubscriptionRefund(Charge charge)
    {
        var invoice = await subscriptionService.StripeSubscriptionInvoiceForPayment(charge.PaymentIntentId);
        if (invoice == null)
            return false;
        // Includes refunds Stripe issues on its own as merchant of record (Managed Payments).
        await subscriptionService.StripeInvoiceRefunded(invoice, charge.Amount, charge.AmountRefunded);
        await ReverseCreatorRevenue(invoice.Id, "charge", charge.Amount > 0 ? charge.AmountRefunded / (decimal)charge.Amount : 0);
        await MarkPaymentRefunded(invoice.Id, "stripe", charge.AmountRefunded / 100m, charge.AmountRefunded >= charge.Amount);
        return true;
    }

    /// <summary>
    /// A refund resource (PAYMENT.SALE.REFUNDED) carries the refunded amount and its own id; a reversal or a sale
    /// in state refunded takes back the whole share. PayPal's refund samples show the amount negative ("-0.01"), so
    /// only its magnitude is used.
    /// </summary>
    private Task ReversePayPalCreatorRevenue(PayPalSale original, PayPalSale webhookResource)
    {
        if (original.State is "refunded" or "reversed")
            return ReverseCreatorRevenue(original.Id, "full", 1);
        var total = Coflnet.Payments.Services.SubscriptionService.ParseMoney(original.Amount?.Total);
        var refunded = webhookResource.SaleId != null ? Coflnet.Payments.Services.SubscriptionService.ParseMoney(webhookResource.Amount?.Total) : null;
        if (total is not > 0 || refunded == null)
            return Task.CompletedTask;
        return ReverseCreatorRevenue(original.Id, webhookResource.Id, Math.Abs(refunded.Value) / total.Value);
    }

    /// <summary>
    /// Takes the creator share of a refunded subscription payment back. Idempotent, so a failure fails the webhook
    /// and the provider's retry repeats it.
    /// </summary>
    private Task ReverseCreatorRevenue(string paymentId, string refundKey, decimal refundedFraction) =>
        _creatorCodeService?.ReverseRevenueAsync(paymentId, refundKey, refundedFraction) ?? Task.CompletedTask;

    private static bool IsPayPalSubscriptionEvent(string eventType) =>
        eventType != null && (eventType.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal)
            || eventType.StartsWith("PAYMENT.SALE.", StringComparison.Ordinal));

    private async Task<IActionResult> HandlePayPalSubscriptionEvent(string eventType, JObject webhook)
    {
        if (webhook["resource"] is not JObject resource)
            return BadRequest();
        var sale = resource.ToObject<PayPalSale>();
        if (!ValidPayPalId(sale?.Id))
            return BadRequest();
        switch (eventType)
        {
            case "PAYMENT.SALE.COMPLETED":
                if (sale.BillingAgreementId == null)
                    return Ok(); // a sale outside a subscription
                if (!ValidPayPalId(sale.BillingAgreementId))
                    return BadRequest();
                await RecordSubscriptionPayment(await subscriptionService.PayPalSaleCompleted(sale), "paypal");
                return Ok();
            case "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED":
                var saleId = sale.SaleId ?? sale.Id;
                if (!ValidPayPalId(saleId))
                    return BadRequest();
                var refunded = await subscriptionService.PayPalSaleRefunded(saleId);
                if (refunded == null)
                    return Ok();
                await ReversePayPalCreatorRevenue(refunded, sale);
                await MarkPaymentRefunded(saleId, "paypal", null, refunded.State is "refunded" or "reversed");
                return Ok();
            case not null when eventType.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal):
                foreach (var outcome in await subscriptionService.PayPalSubscriptionChanged(sale.Id))
                    await RecordSubscriptionPayment(outcome, "paypal");
                return Ok();
            default:
                return Ok();
        }
    }

    /// <summary>
    /// Publishes and records (for tax reporting) a granted subscription payment, and the creator's revenue share.
    /// Idempotent per provider payment: the PaymentRecord is written last and marks the payment as recorded, so a
    /// webhook retried after a failure (e.g. publishing the event) records it then and a duplicate does nothing.
    /// </summary>
    private async Task RecordSubscriptionPayment(SubscriptionPaymentOutcome outcome, string provider)
    {
        if (outcome == null || await db.PaymentRecords.AnyAsync(r => r.Provider == provider
                && r.ExternalOrderId == outcome.PaymentId && r.IsSubscriptionPayment))
            return;
        var subscription = outcome.Subscription;
        var user = subscription.User;
        var method = outcome.TaxRemittedByProcessor ? "stripe_managed_payments" : provider;
        await paymentEventProducer.ProduceEvent(new PaymentEvent
        {
            PayedAmount = (double)outcome.Amount,
            ProductId = outcome.Product.Id.ToString(),
            UserId = user.ExternalId,
            Email = outcome.Email,
            FullName = outcome.Name,
            Address = new Coflnet.Payments.Models.Address { CountryCode = outcome.Country, PostalCode = outcome.PostalCode },
            Currency = outcome.Currency,
            PaymentMethod = method,
            PaymentProvider = provider,
            PaymentProviderTransactionId = outcome.PaymentId,
            Timestamp = outcome.PaidAt
        });
        await RecordSubscriptionCreatorRevenue(outcome);
        await RecordPayment(new PaymentRecord
        {
            UserId = user.Id,
            ExternalUserId = user.ExternalId,
            Country = outcome.Country,
            ZipCode = outcome.PostalCode,
            GrossAmount = outcome.Amount,
            Subtotal = outcome.Amount - outcome.TaxAmount,
            TaxAmount = outcome.TaxAmount,
            TaxRate = outcome.TaxRate,
            TaxName = outcome.TaxAmount > 0 ? "VAT" : null,
            TaxRemittedByProcessor = outcome.TaxRemittedByProcessor,
            Currency = outcome.Currency ?? "EUR",
            Provider = provider,
            PaymentMethod = method,
            ExternalOrderId = outcome.PaymentId,
            ExternalTransactionId = subscription.ExternalId,
            ProductSlug = outcome.Product.Id.ToString(),
            ProductId = outcome.Product.Id,
            CoinAmount = outcome.IsProration ? 0 : (long)outcome.Product.Cost,
            CreatorCode = subscription.CreatorCode,
            PaidAt = outcome.PaidAt,
            Status = PaymentRecordStatus.Confirmed,
            BuyerEmail = outcome.Email,
            BuyerName = outcome.Name,
            IpAddress = outcome.Evidence?.IpAddress,
            CheckoutCountry = outcome.Evidence?.CheckoutCountry,
            CheckoutRegion = outcome.Evidence?.CheckoutRegion,
            BillingRegion = Truncate(outcome.Evidence?.BillingRegion, 16),
            BillingCountry = outcome.Evidence?.BillingCountry,
            PaymentMethodCountry = outcome.Evidence?.PaymentMethodCountry,
            IsSubscriptionPayment = true
        });
    }

    private static string Truncate(string value, int length) => value is { } v && v.Length > length ? v[..length] : value;

    /// <summary>
    /// The share is based on the EUR price agreed for the subscription, not on what a Managed Payments buyer
    /// paid in a local currency.
    /// </summary>
    private async Task RecordSubscriptionCreatorRevenue(SubscriptionPaymentOutcome outcome)
    {
        var code = outcome.Subscription.CreatorCode;
        if (string.IsNullOrWhiteSpace(code) || _creatorCodeService == null)
            return;
        try
        {
            var creatorCode = await _creatorCodeService.GetCreatorCodeAsync(code);
            if (creatorCode == null || await db.CreatorCodeRevenues.AnyAsync(r => r.TransactionReference == outcome.PaymentId))
                return;
            if (outcome.EurAmount is not decimal paid)
            {
                _logger.LogWarning("No EUR amount for subscription payment {PaymentId}; creator revenue not recorded", outcome.PaymentId);
                return;
            }
            var original = outcome.IsProration ? paid : outcome.Product.Price;
            var currency = (outcome.Subscription.Currency ?? outcome.Product.CurrencyCode)?.ToUpperInvariant();
            await _creatorCodeService.RecordRevenueAsync(creatorCode.Id, outcome.Subscription.User.ExternalId, outcome.Product.Id,
                original, Math.Max(0, original - paid), paid, paid * creatorCode.RevenueSharePercent / 100m, currency, outcome.PaymentId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to record creator revenue of subscription payment {PaymentId}", outcome.PaymentId);
        }
    }
}
