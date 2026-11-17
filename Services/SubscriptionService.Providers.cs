using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public partial class SubscriptionService
{
    private const int RefundWindowDays = 3;

    private ISubscriptionProvider ProviderFor(UserSubscription subscription) =>
        providers.TryGetValue(SubscriptionProviders.Normalize(subscription.ProviderSlug), out var provider)
            ? provider
            : throw new ApiException("This subscription's payment provider is not supported.");

    private void DescribeCapabilities(UserSubscription subscription)
    {
        if (!providers.TryGetValue(SubscriptionProviders.Normalize(subscription.ProviderSlug), out var provider))
            return;
        subscription.ProviderSlug = provider.Slug;
        subscription.CanResume = provider.CanResume(subscription);
        subscription.CanChangePlan = provider.CanChangePlan(subscription);
    }

    public Task CancelSubscription(string userId, string subscriptionId) => WithSubscriptionLock(subscriptionId, async () =>
    {
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        await ProviderFor(subscription).CancelAtPeriodEnd(subscription);
        await context.SaveChangesAsync();
        return true;
    });

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <returns>True if successfully resumed</returns>
    public Task<bool> ResumeSubscription(string userId, string subscriptionId) =>
        WithSubscriptionLock(subscriptionId, () => ResumeSubscriptionLocked(userId, subscriptionId));

    private async Task<bool> ResumeSubscriptionLocked(string userId, string subscriptionId)
    {
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        if (subscription.Status != "cancelled")
            throw new ApiException($"Subscription is not cancelled, current status: {subscription.Status}");
        if (subscription.EndsAt.HasValue && subscription.EndsAt.Value < DateTime.UtcNow)
            throw new ApiException("Subscription grace period has expired and cannot be resumed");
        var provider = ProviderFor(subscription);
        if (!provider.CanResume(subscription))
            throw new ApiException("This subscription can not be resumed. Please start a new subscription after it ends.");
        await provider.Resume(subscription);
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Get all invoices for a subscription
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <returns>List of subscription invoices</returns>
    public async Task<IEnumerable<SubscriptionInvoice>> GetSubscriptionInvoices(string userId, string subscriptionId)
    {
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        return await ProviderFor(subscription).GetInvoices(subscription);
    }

    /// <summary>
    /// Refund a subscription invoice payment. Refunds can only be issued up to 3 days after the invoice was created.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID</param>
    /// <param name="invoiceId">The subscription invoice ID</param>
    /// <param name="request">Optional refund amount in cents. If not specified, a full refund will be issued.</param>
    /// <returns>RefundResponse containing the updated invoice details</returns>
    public Task<RefundResponse> RefundSubscriptionPayment(string userId, string subscriptionId, string invoiceId, RefundRequest request) =>
        WithSubscriptionLock(subscriptionId, () => RefundSubscriptionPaymentLocked(userId, subscriptionId, invoiceId, request));

    private async Task<RefundResponse> RefundSubscriptionPaymentLocked(string userId, string subscriptionId, string invoiceId, RefundRequest request)
    {
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        var provider = ProviderFor(subscription);
        var invoice = (await provider.GetInvoices(subscription))?.FirstOrDefault(i => i.Id == invoiceId)
            ?? throw new ApiException("Invoice not found");
        if (invoice.Refunded || invoice.Status == "refunded")
        {
            await ApplyInvoiceRefund(subscription, invoice.Id, invoice.BillingReason, invoice.CreatedAt, true);
            return new RefundResponse { Id = invoice.Id, Refunded = true, RefundedAmount = invoice.RefundedAmount, Status = invoice.Status };
        }
        EnsureRefundable(invoice, request?.Amount);

        var refundResponse = await provider.RefundInvoice(subscription, invoice, request?.Amount)
            ?? throw new ApiException("Failed to process refund. Please try again later.");
        await ApplyInvoiceRefund(subscription, invoice.Id, invoice.BillingReason, invoice.CreatedAt,
            refundResponse.Refunded || refundResponse.Status == "refunded" || refundResponse.RefundedAmount >= invoice.Total);
        logger.LogInformation("Subscription payment refunded for user {UserId}, subscription {SubscriptionId}, invoice {InvoiceId}: amount={Amount}",
            userId, subscriptionId, invoiceId, refundResponse.RefundedAmount);
        return refundResponse;
    }

    /// <summary>
    /// A partial refund keeps the paid period, so this endpoint only refunds the whole remaining amount;
    /// otherwise refunding all but one cent would keep the service. Partial refunds go through the provider dashboard.
    /// </summary>
    private static void EnsureRefundable(SubscriptionInvoice invoice, int? amount)
    {
        if (invoice.Status is not ("paid" or "partial_refund") || invoice.Total <= 0
            || amount <= 0 || amount > invoice.Total - invoice.RefundedAmount)
            throw new ApiException("Invalid invoice refund amount or status.");
        if (amount != null && amount != invoice.Total - invoice.RefundedAmount)
            throw new ApiException("Only the full remaining invoice amount can be refunded here.");
        var daysSinceCreation = (DateTime.UtcNow - invoice.CreatedAt).TotalDays;
        if (daysSinceCreation > RefundWindowDays)
            throw new ApiException($"Refund window has expired. Invoices can only be refunded within 3 days of creation. This invoice was created {daysSinceCreation:F1} days ago.");
    }

    /// <summary>
    /// Lemon Squeezy implementation; it shares the plan reconciliation code of this service.
    /// </summary>
    private sealed class LemonSqueezyProvider(SubscriptionService service) : ISubscriptionProvider
    {
        private LemonSqueezyService Api => service.lemonSqueezyService;

        public string Slug => SubscriptionProviders.LemonSqueezy;

        public bool CanResume(UserSubscription subscription) => true;

        public bool CanChangePlan(UserSubscription subscription) => true;

        public bool OffersPlan(TopUpProduct plan) => Api.SubscriptionVariants.ContainsKey(plan.Slug);

        public async Task CancelAtPeriodEnd(UserSubscription subscription)
        {
            var remote = await Api.CancelSubscription(subscription.ExternalId);
            if (remote.Attributes.Status != "cancelled" || remote.Attributes.EndsAt == null)
                throw new ApiException("Lemon Squeezy did not confirm the cancellation.");
            subscription.Status = remote.Attributes.Status;
            subscription.EndsAt = remote.Attributes.EndsAt;
            subscription.UpdatedAt = remote.Attributes.UpdatedAt;
        }

        public async Task Resume(UserSubscription subscription)
        {
            var remote = await Api.ResumeSubscription(subscription.ExternalId);
            if (remote.Attributes.Status != "active" || remote.Attributes.EndsAt != null)
                throw new ApiException("Lemon Squeezy did not confirm reactivation.");
            subscription.Status = remote.Attributes.Status;
            subscription.EndsAt = null;
            subscription.UpdatedAt = remote.Attributes.UpdatedAt;
        }

        public Task<SubscriptionChangeResult> ChangePlan(UserSubscription subscription, TopUpProduct target) =>
            service.ChangeLemonSqueezyPlan(subscription, target);

        public async Task<IReadOnlyList<SubscriptionInvoice>> GetInvoices(UserSubscription subscription)
        {
            var invoices = await Api.GetSubscriptionInvoicesAsync(subscription.ExternalId);
            return invoices?.Where(i => i.SubscriptionId.ToString() == subscription.ExternalId).ToList()
                ?? new List<SubscriptionInvoice>();
        }

        public async Task<RefundResponse> RefundInvoice(UserSubscription subscription, SubscriptionInvoice invoice, int? amountCents)
        {
            if (invoice.BillingReason == "updated")
                await service.SynchronizePlan(subscription, await Api.GetSubscription(subscription.ExternalId));
            return await Api.RefundInvoiceAsync(invoice.Id, amountCents);
        }
    }
}
