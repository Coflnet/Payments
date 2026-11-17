using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Stripe;
using Stripe.Checkout;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Stripe.net backed <see cref="IStripeSubscriptionGateway"/> (uses the global StripeConfiguration.ApiKey)
/// </summary>
public class StripeSubscriptionGateway : IStripeSubscriptionGateway
{
    private static readonly List<string> ExpandPrice = ["items.data.price"];
    private readonly string taxCode;

    /// <param name="config">STRIPE:MANAGED_TAX_CODE is set on the plan products (required for Managed Payments)</param>
    public StripeSubscriptionGateway(Microsoft.Extensions.Configuration.IConfiguration config = null)
    {
        taxCode = StripeManagedSettings.From(config).TaxCode;
    }

    public Task<Session> CreateCheckoutSession(SessionCreateOptions options) =>
        new SessionService().CreateAsync(options);

    public Task<Session> GetCheckoutSession(string sessionId) =>
        new SessionService().GetAsync(sessionId);

    public async Task<StripeSubscriptionSnapshot> GetSubscription(string subscriptionId) =>
        Snapshot(await new Stripe.SubscriptionService().GetAsync(subscriptionId,
            new SubscriptionGetOptions { Expand = ExpandPrice }));

    public async Task<StripeSubscriptionSnapshot> SetCancelAtPeriodEnd(string subscriptionId, bool cancel) =>
        Snapshot(await new Stripe.SubscriptionService().UpdateAsync(subscriptionId,
            new SubscriptionUpdateOptions { CancelAtPeriodEnd = cancel, Expand = ExpandPrice }));

    public async Task<StripeSubscriptionSnapshot> CancelNow(string subscriptionId) =>
        Snapshot(await new Stripe.SubscriptionService().CancelAsync(subscriptionId,
            new SubscriptionCancelOptions { Expand = ExpandPrice }));

    public async Task<StripePriceChange> ChangePrice(StripeSubscriptionSnapshot current, StripePriceChangeRequest request)
    {
        var options = new SubscriptionUpdateOptions
        {
            Items =
            [
                new SubscriptionItemOptions
                {
                    Id = current.ItemId,
                    PriceData = new SubscriptionItemPriceDataOptions
                    {
                        Product = await EnsurePlanProduct(request.PlanSlug, request.PlanName),
                        Currency = request.Currency,
                        UnitAmount = request.UnitAmount,
                        TaxBehavior = request.TaxBehavior,
                        Recurring = new SubscriptionItemPriceDataRecurringOptions
                            { Interval = "week", IntervalCount = request.IntervalWeeks }
                    }
                }
            ],
            ProrationBehavior = request.InvoiceNow ? "always_invoice" : "none",
            // An upgrade only applies once its proration invoice is paid, also for delayed payment methods
            // (error_if_incomplete would apply a still processing payment). Pending updates can not change metadata,
            // which is why the plan is identified by the item's product.
            PaymentBehavior = request.InvoiceNow ? "pending_if_incomplete" : "allow_incomplete",
            Expand = ["latest_invoice", .. ExpandPrice]
        };
        var updated = await new Stripe.SubscriptionService().UpdateAsync(current.Id, options);
        var invoice = request.InvoiceNow ? updated.LatestInvoice : null;
        return new StripePriceChange(Snapshot(updated), invoice?.Id, invoice?.Status);
    }

    private async Task<string> EnsurePlanProduct(string planSlug, string planName)
    {
        var id = StripePlans.ProductId(planSlug);
        var products = new Stripe.ProductService();
        try
        {
            return (await products.GetAsync(id)).Id;
        }
        catch (StripeException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var created = await products.CreateAsync(new ProductCreateOptions
            {
                Id = id, Name = planName, TaxCode = taxCode,
                Metadata = new Dictionary<string, string> { [SubscriptionService.StripeMetadata.ProductSlug] = planSlug }
            });
            return created.Id;
        }
    }

    public async Task<IReadOnlyList<SubscriptionInvoice>> ListInvoices(string subscriptionId)
    {
        var invoices = await new InvoiceService().ListAsync(new InvoiceListOptions { Subscription = subscriptionId, Limit = 100 });
        return invoices.Data.Select(ToSubscriptionInvoice).ToList();
    }

    public async Task<RefundResponse> RefundInvoice(string invoiceId, long? amountCents)
    {
        var payments = await new InvoicePaymentService().ListAsync(new InvoicePaymentListOptions { Invoice = invoiceId });
        var paymentIntent = payments.Data.FirstOrDefault(p => p.Status == "paid")?.Payment?.PaymentIntentId
            ?? throw new ApiException("The invoice has no refundable payment.");
        var refund = await new RefundService().CreateAsync(new RefundCreateOptions { PaymentIntent = paymentIntent, Amount = amountCents });
        var invoice = await new InvoiceService().GetAsync(invoiceId);
        var refunded = (int)refund.Amount;
        return new RefundResponse
        {
            Id = invoiceId, Refunded = refunded >= invoice.AmountPaid, RefundedAmount = refunded,
            Status = refund.Status == "failed" ? "failed" : refunded >= invoice.AmountPaid ? "refunded" : "partial_refund"
        };
    }

    public async Task<StripeInvoiceInfo> FindInvoiceByPaymentIntent(string paymentIntentId)
    {
        var payments = await new InvoicePaymentService().ListAsync(new InvoicePaymentListOptions
        {
            Payment = new InvoicePaymentPaymentOptions { Type = "payment_intent", PaymentIntent = paymentIntentId }
        });
        var invoiceId = payments.Data.FirstOrDefault()?.InvoiceId;
        if (invoiceId == null)
            return null;
        return StripeInvoiceInfo.From(await new InvoiceService().GetAsync(invoiceId));
    }

    private static SubscriptionInvoice ToSubscriptionInvoice(Invoice invoice)
    {
        var info = StripeInvoiceInfo.From(invoice);
        return new SubscriptionInvoice
        {
            Id = invoice.Id,
            BillingReason = info.NormalizedBillingReason,
            Currency = invoice.Currency?.ToUpperInvariant(),
            Status = invoice.Status,
            StatusFormatted = invoice.Status,
            Subtotal = (int)invoice.Subtotal,
            Tax = (int)(invoice.TotalTaxes?.Sum(t => t.Amount) ?? 0),
            Total = (int)invoice.Total,
            UserEmail = invoice.CustomerEmail,
            UserName = invoice.CustomerName,
            InvoiceUrl = invoice.HostedInvoiceUrl,
            CreatedAt = invoice.Created,
            UpdatedAt = invoice.Created
        };
    }

    internal static StripeSubscriptionSnapshot Snapshot(Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        return new StripeSubscriptionSnapshot(subscription.Id, subscription.Status,
            subscription.CancelAtPeriodEnd, subscription.CancelAt,
            item?.CurrentPeriodEnd, subscription.EndedAt, subscription.CustomerId,
            new Dictionary<string, string>(subscription.Metadata ?? new Dictionary<string, string>()),
            item?.Id, item?.Price?.Id, item?.Price?.ProductId, item?.Price?.UnitAmount, item?.Price?.Currency,
            item?.Price?.TaxBehavior is "inclusive" or "exclusive" ? item.Price.TaxBehavior : null,
            subscription.ManagedPayments?.Enabled == true, subscription.Created, subscription.PendingUpdate != null);
    }
}
