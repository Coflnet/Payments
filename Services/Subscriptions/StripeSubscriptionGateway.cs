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
    private readonly Microsoft.Extensions.Logging.ILogger<StripeSubscriptionGateway> logger;

    /// <param name="config">STRIPE:MANAGED_TAX_CODE is set on the plan products (required for Managed Payments)</param>
    public StripeSubscriptionGateway(Microsoft.Extensions.Configuration.IConfiguration config = null,
        Microsoft.Extensions.Logging.ILogger<StripeSubscriptionGateway> logger = null)
    {
        this.logger = logger;
        taxCode = StripeManagedSettings.From(config).TaxCode;
    }

    public Task<Session> CreateCheckoutSession(SessionCreateOptions options) =>
        new SessionService().CreateAsync(options);

    public Task<Session> GetCheckoutSession(string sessionId) =>
        new SessionService().GetAsync(sessionId);

    public async Task<string> GetDisputeStatus(string disputeId) =>
        (await new DisputeService().GetAsync(disputeId)).Status;

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

    public Task<string> ScheduleRenewal(StripeSubscriptionSnapshot current, StripeRenewalRequest request) =>
        StripeRenewalSchedule.Schedule(current, request, new ScheduleCalls(this), DateTime.UtcNow);

    public async Task<StripeScheduleInfo> GetSchedule(string scheduleId)
    {
        var schedule = await new SubscriptionScheduleService().GetAsync(scheduleId);
        return new StripeScheduleInfo(schedule.Id, StripeRenewalSchedule.HasForeignPhases(schedule, DateTime.UtcNow),
            StripeRenewalSchedule.IsRenewalSchedule(schedule), schedule.Metadata?.GetValueOrDefault(StripeRenewalSchedule.MarkerKey));
    }

    private sealed class ScheduleCalls(StripeSubscriptionGateway gateway) : IStripeScheduleCalls
    {
        private readonly SubscriptionScheduleService schedules = new();
        public Task<SubscriptionSchedule> CreateFrom(string subscriptionId) =>
            schedules.CreateAsync(new SubscriptionScheduleCreateOptions { FromSubscription = subscriptionId });
        public Task<SubscriptionSchedule> Get(string scheduleId) => schedules.GetAsync(scheduleId);
        public Task<SubscriptionSchedule> Update(string scheduleId, SubscriptionScheduleUpdateOptions options) =>
            schedules.UpdateAsync(scheduleId, options);
        public Task Release(string scheduleId) => schedules.ReleaseAsync(scheduleId);
        public Task<string> EnsurePlanProduct(string planSlug, string planName) => gateway.EnsurePlanProduct(planSlug, planName);
    }

    public async Task<StripeSubscriptionSnapshot> ReleaseSchedule(string scheduleId, string subscriptionId)
    {
        await new SubscriptionScheduleService().ReleaseAsync(scheduleId);
        return await GetSubscription(subscriptionId);
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

    public Task<string> EnsureFirstPaymentCoupon(StripeCouponSpec spec)
    {
        var coupons = new CouponService();
        return StripeFirstPaymentCoupon.Resolve(spec, id => GetCoupon(coupons, id), versioned => CreateCoupon(coupons, versioned), logger);
    }

    private static async Task<Coupon> GetCoupon(CouponService coupons, string id)
    {
        try
        {
            return await coupons.GetAsync(id);
        }
        catch (StripeException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<string> CreateCoupon(CouponService coupons, StripeCouponSpec spec)
    {
        try
        {
            var created = await coupons.CreateAsync(new CouponCreateOptions
            {
                Id = spec.Id,
                Duration = "once",
                AmountOff = spec.AmountOffCents,
                PercentOff = spec.PercentOff,
                Currency = spec.AmountOffCents == null ? null : spec.Currency,
                Name = "First payment discount"
            });
            return created.Id;
        }
        catch (StripeException e) when (e.StripeError?.Code == "resource_already_exists")
        {
            return spec.Id; // a concurrent checkout created the same coupon
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> VatTaxRates = new();
    private const string VatRateMetadataKey = "coflnet_vat";

    public async Task<string> EnsureVatTaxRate(string country, int basisPoints)
    {
        var key = $"{country.ToUpperInvariant()}_{basisPoints}";
        if (VatTaxRates.TryGetValue(key, out var cached))
            return cached;
        var rates = new TaxRateService();
        var existing = await FindVatTaxRate(rates, key);
        var id = existing ?? (await rates.CreateAsync(new TaxRateCreateOptions
        {
            DisplayName = "VAT",
            Jurisdiction = country.ToUpperInvariant(),
            Percentage = basisPoints / 100m,
            Inclusive = true,
            Metadata = new Dictionary<string, string> { [VatRateMetadataKey] = key }
        })).Id;
        VatTaxRates[key] = id;
        return id;
    }

    private static async Task<string> FindVatTaxRate(TaxRateService rates, string key)
    {
        await foreach (var rate in rates.ListAutoPagingAsync(new TaxRateListOptions { Active = true, Inclusive = true, Limit = 100 }))
        {
            if (rate.Metadata?.GetValueOrDefault(VatRateMetadataKey) == key)
                return rate.Id;
        }
        return null;
    }

    public async Task<string> GetCardCountry(string invoiceId)
    {
        var payments = await new InvoicePaymentService().ListAsync(new InvoicePaymentListOptions { Invoice = invoiceId });
        var paymentIntent = payments.Data.FirstOrDefault(p => p.Status == "paid")?.Payment?.PaymentIntentId;
        if (paymentIntent == null)
            return null;
        var intent = await new PaymentIntentService().GetAsync(paymentIntent, new PaymentIntentGetOptions { Expand = ["latest_charge"] });
        return intent.LatestCharge?.PaymentMethodDetails?.Card?.Country?.ToUpperInvariant();
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
            subscription.ManagedPayments?.Enabled == true, subscription.Created, subscription.PendingUpdate != null,
            subscription.ScheduleId);
    }
}
