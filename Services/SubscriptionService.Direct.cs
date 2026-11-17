using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// A provider payment that granted a paid period; the caller records it for tax reporting.
/// </summary>
/// <param name="Subscription">The local subscription (with user and product loaded)</param>
/// <param name="Product">The plan that was granted</param>
/// <param name="PaymentId">Provider invoice/sale id</param>
/// <param name="Amount">Paid gross amount in major units of <paramref name="Currency"/></param>
/// <param name="Currency">Upper case ISO currency the buyer paid in</param>
/// <param name="Country">Buyer country if known</param>
/// <param name="PostalCode">Buyer postal code if known</param>
/// <param name="Email">Buyer email if known</param>
/// <param name="Name">Buyer name if known</param>
/// <param name="PaidAt">When the provider created the payment</param>
/// <param name="EurAmount">The payment in the plan currency (EUR) for revenue shares; null when unknown
/// (a Managed Payments proration paid in a local currency)</param>
/// <param name="IsProration">An upgrade's proration, not a paid period</param>
public record SubscriptionPaymentOutcome(UserSubscription Subscription, TopUpProduct Product, string PaymentId,
    decimal Amount, string Currency, string Country, string PostalCode, string Email, string Name, DateTime PaidAt,
    decimal? EurAmount = null, bool IsProration = false)
{
    /// <summary>Merchant of record (Stripe Managed Payments) remits the tax</summary>
    public bool TaxRemittedByProcessor => Subscription.ManagedPayments;
}

public partial class SubscriptionService
{
    /// <summary>
    /// Grants one paid subscription period: credits the plan's coins and immediately spends them on the
    /// service, which extends the ownership (or tier slots) bound to <paramref name="subscriptionId"/>.
    /// Idempotent per <paramref name="reference"/>: returns false when that payment was already granted.
    /// </summary>
    public async Task<bool> GrantPaidPeriod(string userId, TopUpProduct product, string subscriptionId, string reference)
    {
        if (await IsGranted(userId, reference))
            return false;
        try
        {
            await GrantPaidPeriodCore(userId, product, subscriptionId, reference);
            return true;
        }
        catch (TransactionService.DupplicateTransactionException)
        {
            return false;
        }
    }

    private Task<bool> IsGranted(string userId, string reference) => context.FiniteTransactions
        .AnyAsync(t => t.User.ExternalId == userId && (t.Reference == reference || t.Reference == reference + "-topup"));

    private async Task GrantPaidPeriodCore(string userId, TopUpProduct product, string subscriptionId, string reference)
    {
        await using var transaction = await transactionService.StartDbTransaction();
        try
        {
            await transactionService.AddTopUp(product.Id, userId, reference + "-topup");
            await transactionService.PurchaseService(product.Slug, userId, 1, reference, product, subscriptionId: subscriptionId);
            await transaction.CommitAsync();
            logger.LogInformation("Granted subscription {SubscriptionId} period of {ProductSlug} to user {UserId} ({Reference})",
                subscriptionId, product.Slug, userId, reference);
        }
        catch (Exception e)
        {
            if (e is not TransactionService.DupplicateTransactionException)
                logger.LogError(e, "Error processing topup/purchase");
            await transaction.RollbackAsync();
            throw;
        }
    }

    private Task<UserSubscription> FindProviderSubscription(string providerSlug, string externalId) => context.Subscriptions
        .Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
        .Where(s => s.ProviderSlug == providerSlug && s.ExternalId == externalId)
        .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();

    private async Task<TopUpProduct> PlanBySlug(string slug)
    {
        var product = string.IsNullOrWhiteSpace(slug) ? null
            : await context.TopUpProducts.Include(p => p.Groups).SingleOrDefaultAsync(p => p.Slug == slug);
        return IsSubscriptionPlan(product) ? product : null;
    }

    private async Task<PaymentRequest> MarkCheckoutPaid(string provider, string sessionId)
    {
        var request = await context.PaymentRequests.Include(r => r.User).Include(r => r.ProductId)
            .Where(r => r.Provider == provider && r.SessionId == sessionId)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (request == null || request.State >= PaymentRequest.Status.PAID)
            return request;
        request.State = PaymentRequest.Status.PAID;
        request.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return request;
    }

    /// <summary>
    /// A provider took money this service can not fulfil. It is logged as an error and recorded as a
    /// REFUND_PENDING payment request (keyed by the provider payment id), which the pending refund metric reports.
    /// </summary>
    private async Task MarkForManualRefund(string provider, string paymentId, UserSubscription subscription, decimal amount, string reason)
    {
        logger.LogError("Paid {Provider} payment {PaymentId} was not granted and requires a manual refund: {Reason}",
            provider, paymentId, reason);
        var request = await context.PaymentRequests.FirstOrDefaultAsync(r => r.Provider == provider && r.SessionId == paymentId);
        if (request == null)
        {
            request = new PaymentRequest
            {
                Provider = provider, SessionId = paymentId, Amount = amount, User = subscription?.User,
                ProductId = subscription?.Product as TopUpProduct, CreatedAt = DateTime.UtcNow
            };
            context.PaymentRequests.Add(request);
        }
        request.State = PaymentRequest.Status.REFUND_PENDING;
        request.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }
}
