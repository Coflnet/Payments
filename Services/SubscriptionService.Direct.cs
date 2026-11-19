using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// The signals of where a buyer is, kept per payment as place of supply evidence (VAT)
/// </summary>
/// <param name="IpAddress">IP the checkout was started from</param>
/// <param name="CheckoutCountry">The verified country whose VAT the price contains</param>
/// <param name="BillingCountry">Stripe: the country of the billing address entered at checkout; PayPal: the payer's country</param>
/// <param name="PaymentMethodCountry">Stripe: the issuing country of the card</param>
/// <param name="CheckoutRegion">The buyer's state at checkout (from the edge), evidence for the per-state US cap</param>
/// <param name="BillingRegion">Stripe: the state of the billing address entered at checkout (evidence only, never rejected on mismatch)</param>
public record PaymentEvidence(string IpAddress, string CheckoutCountry, string BillingCountry, string PaymentMethodCountry,
    string CheckoutRegion = null, string BillingRegion = null);

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
/// <param name="TaxAmount">VAT contained in <paramref name="Amount"/> (direct sales: our VAT; Managed Payments: what Stripe remitted)</param>
/// <param name="TaxRate">VAT rate in percent, 0 when unknown</param>
/// <param name="Evidence">Where the buyer was (place of supply evidence), null for prorations</param>
/// <param name="IsRenewal">False for the subscription's first payment, true for later ones</param>
/// <param name="PriceTaxBehavior">Managed Payments: the Stripe price's tax_behavior ("exclusive": net, "inclusive"), null when unknown</param>
public record SubscriptionPaymentOutcome(UserSubscription Subscription, TopUpProduct Product, string PaymentId,
    decimal Amount, string Currency, string Country, string PostalCode, string Email, string Name, DateTime PaidAt,
    decimal? EurAmount = null, bool IsProration = false, decimal TaxAmount = 0, double TaxRate = 0,
    PaymentEvidence Evidence = null, bool IsRenewal = false, string PriceTaxBehavior = null)
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

    /// <summary>
    /// What the provider charges for a net price: Managed Payments the net price itself (Stripe adds the tax),
    /// direct subscriptions the net price plus the VAT of <paramref name="country"/>. Null when a direct country has no VAT rate.
    /// </summary>
    private long? ChargedCents(decimal netPrice, bool managedPayments, string country)
    {
        if (managedPayments)
            return SubscriptionPricing.ToCents(netPrice);
        return tax?.RateFor(country) is int basisPoints ? SubscriptionPricing.GrossCents(netPrice, basisPoints) : null;
    }

    /// <summary>
    /// Splits a gross amount a direct sale charged into its net part and VAT (zero without a rate)
    /// </summary>
    private (decimal net, decimal vat, double ratePercent) VatSplit(decimal gross, string country)
    {
        var basisPoints = tax?.RateFor(country) ?? 0;
        var grossCents = SubscriptionPricing.ToCents(gross);
        var vat = SubscriptionPricing.VatPortionCents(grossCents, basisPoints) / 100m;
        return (gross - vat, vat, basisPoints / 100.0);
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
        .Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups).Include(s => s.RenewalProduct).ThenInclude(p => p.Groups)
        .Where(s => s.ProviderSlug == providerSlug && s.ExternalId == externalId)
        .OrderByDescending(s => s.UpdatedAt).FirstOrDefaultAsync();

    /// <summary>
    /// Saves a new local row for a provider subscription. Providers send several events for a new subscription within a
    /// second (Stripe: checkout.session.completed, invoice.paid, customer.subscription.created), and the subscription lock only
    /// holds once a row exists, so a concurrent handler may insert the same row first. Its unique index
    /// (ProviderSlug, ExternalId) then rejects this insert; the failed row is dropped and the existing one returned,
    /// so the handler continues instead of failing until the provider retries.
    /// Other pending changes of the context stay tracked and are saved by the caller's next save.
    /// </summary>
    private async Task<UserSubscription> GetOrCreateSubscriptionAsync(UserSubscription candidate)
    {
        context.Subscriptions.Add(candidate);
        try
        {
            await context.SaveChangesAsync();
            return candidate;
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            context.Entry(candidate).State = EntityState.Detached;
            var existing = await FindProviderSubscription(candidate.ProviderSlug, candidate.ExternalId);
            if (existing == null)
                throw;
            logger.LogInformation("{Provider} subscription {SubscriptionId} was recorded by a concurrent webhook; continuing with that row",
                candidate.ProviderSlug, candidate.ExternalId);
            return existing;
        }
    }

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
    /// REFUND_PENDING payment request (keyed by the provider payment id, with the checkout country), which the pending refund
    /// metric reports and the revenue export lists (includeAllStatuses) as RefundPending.
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
        // The VAT country of the sale, so the revenue export can list it without a PaymentRecord
        request.Country ??= subscription?.CheckoutCountry;
        request.CheckoutRegion ??= subscription?.CheckoutRegion;
        request.State = PaymentRequest.Status.REFUND_PENDING;
        request.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }
}
