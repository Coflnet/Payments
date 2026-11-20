using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Chargebacks of direct subscription payments (Stripe invoices, PayPal sales). A dispute takes the paid period back exactly
/// like a full refund (<see cref="ApplyInvoiceRefund"/>, so a refund that already did it makes the dispute a no-op) and ends
/// the subscription at the provider, because the buyer disputes the service. Winning the dispute grants the period again once;
/// the cancelled subscription stays cancelled.
/// </summary>
public partial class SubscriptionService
{
    private const string DisputeWonSuffix = "-disputewon";

    private record DisputedPayment(string Id, string BillingReason, DateTime CreatedAt);

    /// <summary>The disputed charge paid this Stripe invoice</summary>
    public Task StripeInvoiceDisputed(StripeInvoiceInfo invoice) =>
        WithDisputedStripeSubscription(invoice, subscription => ApplyDispute(subscription, StripePayment(invoice)));

    /// <summary>The dispute over this Stripe invoice's charge was won</summary>
    public Task StripeInvoiceDisputeWon(StripeInvoiceInfo invoice) =>
        WithDisputedStripeSubscription(invoice, subscription => ApplyDisputeWon(subscription, StripePayment(invoice)));

    private static DisputedPayment StripePayment(StripeInvoiceInfo invoice) =>
        new(invoice.Id, invoice.NormalizedBillingReason, invoice.Created);

    private Task WithDisputedStripeSubscription(StripeInvoiceInfo invoice, Func<UserSubscription, Task> action) =>
        WithSubscriptionLock(invoice.SubscriptionId, async () =>
        {
            var subscription = await FindProviderSubscription(SubscriptionProviders.Stripe, invoice.SubscriptionId)
                ?? throw new ApiException("Disputed Stripe subscription is unknown; retry after it was recorded.");
            await action(subscription);
            return true;
        });

    /// <summary>
    /// A PayPal sale was disputed or reversed; false when the sale is unknown to PayPal or not part of a subscription.
    /// </summary>
    public Task<bool> PayPalSaleDisputed(string saleId) => WithPayPalDisputedSale(saleId, ApplyDispute);

    /// <summary>A PayPal dispute was decided for the seller</summary>
    public Task<bool> PayPalSaleDisputeWon(string saleId) => WithPayPalDisputedSale(saleId, ApplyDisputeWon);

    /// <summary>The seller still has the money of the sale (state completed), e.g. after a dispute resolved with a payout</summary>
    public async Task<bool> PayPalSaleKept(string saleId) => (await FindPayPalSale(saleId))?.State == "completed";

    /// <summary>The live state of a dispute; null when PayPal does not know it</summary>
    public async Task<PayPalDispute> PayPalDispute(string disputeId)
    {
        try
        {
            return await PayPalApi.GetDispute(disputeId);
        }
        catch (Exception e) when (IsPayPalNotFound(e))
        {
            return null;
        }
    }

    /// <summary>The live status of a Stripe dispute; null when unknown to Stripe (another account's, deleted) or Stripe is not configured</summary>
    public async Task<string> StripeDisputeStatus(string disputeId)
    {
        if (stripe == null)
            return null;
        try
        {
            return await stripe.GetDisputeStatus(disputeId);
        }
        catch (Stripe.StripeException e) when (e.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Whether a dispute reverted this PayPal sale (the ledger note of <see cref="ApplyDispute"/>)</summary>
    public async Task<bool> PayPalSaleWasDisputed(string saleId)
    {
        var sale = await FindPayPalSale(saleId);
        if (sale?.Id != saleId || string.IsNullOrEmpty(sale.BillingAgreementId))
            return false;
        var subscription = await FindProviderSubscription(SubscriptionProviders.PayPal, sale.BillingAgreementId);
        return subscription != null && await transactionService.HasLedgerMarker(subscription.User, DisputeOpenedReference(saleId));
    }

    private static bool IsPayPalNotFound(Exception e) => e is PayPalHttp.HttpException { StatusCode: System.Net.HttpStatusCode.NotFound };

    private async Task<PayPalSale> FindPayPalSale(string saleId)
    {
        try
        {
            return await PayPalApi.GetSale(saleId);
        }
        catch (Exception e) when (IsPayPalNotFound(e))
        {
            return null;
        }
    }

    private async Task<bool> WithPayPalDisputedSale(string saleId, Func<UserSubscription, DisputedPayment, Task> action)
    {
        var sale = await FindPayPalSale(saleId);
        if (sale?.Id != saleId || string.IsNullOrEmpty(sale.BillingAgreementId))
            return false;
        return await WithSubscriptionLock(sale.BillingAgreementId, async () =>
        {
            var subscription = await FindProviderSubscription(SubscriptionProviders.PayPal, sale.BillingAgreementId);
            if (subscription == null)
            {
                logger.LogWarning("Dispute of PayPal sale {SaleId} of unknown subscription {SubscriptionId}", saleId, sale.BillingAgreementId);
                return false;
            }
            await action(subscription, new DisputedPayment(sale.Id, "payment", ParseTime(sale.CreateTime) ?? DateTime.UtcNow));
            return true;
        });
    }

    internal static string DisputeOpenedReference(string paymentId) => "dispute-opened " + paymentId;
    private static string DisputeWonMarker(string paymentId) => "dispute-won " + paymentId;
    private static string DisputeRefundedMarker(string paymentId) => "dispute-refunded " + paymentId;

    /// <summary>
    /// A refund that finds the invoice reverted by a still open dispute: winning that dispute must not grant it again
    /// </summary>
    private async Task NoteRefundAfterDispute(UserSubscription subscription, string paymentId)
    {
        var user = subscription.User;
        if (await transactionService.HasLedgerMarker(user, DisputeOpenedReference(paymentId))
            && !await transactionService.HasLedgerMarker(user, DisputeWonMarker(paymentId)))
            await transactionService.AddLedgerMarker(user, DisputeRefundedMarker(paymentId));
    }

    /// <summary>
    /// Reverts the payment unless a refund did it before (the ledger tells which one reverted it), then cancels. Every step is
    /// repeatable, so a webhook retried after a failure finishes the job.
    /// </summary>
    private async Task ApplyDispute(UserSubscription subscription, DisputedPayment payment)
    {
        var provider = subscription.ProviderSlug;
        var user = subscription.User;
        var hadMarker = await transactionService.HasLedgerMarker(user, DisputeOpenedReference(payment.Id));
        var revertedByRefund = !hadMarker && await context.RefundedSubscriptionInvoices.AnyAsync(i => i.InvoiceId == payment.Id);
        if (revertedByRefund || await transactionService.HasLedgerMarker(user, DisputeWonMarker(payment.Id)))
        {
            logger.LogInformation("Dispute of {Provider} payment {PaymentId} ignored: already refunded or decided for the seller", provider, payment.Id);
            return;
        }
        await ApplyInvoiceRefund(subscription, payment.Id, payment.BillingReason, payment.CreatedAt, true, byDispute: true);
        await CancelImmediately(subscription);
        PaymentRecordDispute.Open(await PaymentRecordDispute.Find(context, provider, payment.Id));
        await context.SaveChangesAsync();
        if (hadMarker)
            return;
        PaymentMetrics.Disputes.WithLabels(provider, "opened").Inc();
        logger.LogWarning("Disputed {Provider} payment {PaymentId} of subscription {SubscriptionId} reverted and the subscription cancelled",
            provider, payment.Id, subscription.ExternalId);
    }

    private async Task ApplyDisputeWon(UserSubscription subscription, DisputedPayment payment)
    {
        var provider = subscription.ProviderSlug;
        var user = subscription.User;
        if (await transactionService.HasLedgerMarker(user, DisputeWonMarker(payment.Id)))
            return;
        if (!await transactionService.HasLedgerMarker(user, DisputeOpenedReference(payment.Id))
            || await transactionService.HasLedgerMarker(user, DisputeRefundedMarker(payment.Id)))
        {
            logger.LogInformation("Won dispute of {Provider} payment {PaymentId} needs no restore: no dispute reverted it, or a refund did meanwhile", provider, payment.Id);
            return;
        }
        await RestorePeriod(subscription, payment);
        var marker = await context.RefundedSubscriptionInvoices.FirstOrDefaultAsync(i => i.InvoiceId == payment.Id);
        if (marker != null)
            context.RefundedSubscriptionInvoices.Remove(marker);
        PaymentRecordDispute.Won(await PaymentRecordDispute.Find(context, provider, payment.Id));
        await context.SaveChangesAsync();
        await transactionService.AddLedgerMarker(user, DisputeWonMarker(payment.Id));
        PaymentMetrics.Disputes.WithLabels(provider, "won").Inc();
        logger.LogInformation("Won dispute of {Provider} payment {PaymentId}: period of subscription {SubscriptionId} restored",
            provider, payment.Id, subscription.ExternalId);
    }

    private static string DisputeWonReference(UserSubscription subscription, DisputedPayment payment) =>
        SubscriptionProviders.GrantReference(subscription.ProviderSlug, payment.Id) + DisputeWonSuffix;

    /// <summary>
    /// A refund after a won dispute also takes back the period the win granted again (the original grant's revert is idempotent
    /// and would otherwise leave the re-grant standing)
    /// </summary>
    private async Task RevertDisputeRegrant(UserSubscription subscription, string paymentId)
    {
        var reference = SubscriptionProviders.GrantReference(subscription.ProviderSlug, paymentId) + DisputeWonSuffix;
        var userId = subscription.User.Id;
        if (await context.FiniteTransactions.AnyAsync(t => t.User.Id == userId && t.Reference == reference))
            await RevertPurchase(subscription.User.ExternalId, reference);
        if (await context.FiniteTransactions.AnyAsync(t => t.User.Id == userId && t.Reference == reference + "-topup"))
            await RevertPurchase(subscription.User.ExternalId, reference + "-topup", adjustTime: false);
    }

    /// <summary>
    /// Undoes <see cref="RevertUpdatedInvoice"/>/<see cref="RevertPaidPeriod"/>: a plan change is applied again, a period is
    /// granted again under its own reference (so it happens once; the original grant stays booked next to its revert)
    /// </summary>
    private async Task RestorePeriod(UserSubscription subscription, DisputedPayment payment)
    {
        var reference = SubscriptionProviders.GrantReference(subscription.ProviderSlug, payment.Id);
        if (payment.BillingReason == "updated" && await RestorePlanChange(subscription, payment.Id))
            return;
        if (!await context.FiniteTransactions.AnyAsync(t => t.User.Id == subscription.User.Id && t.Reference == reference))
            reference = subscription.ExternalId + payment.CreatedAt.Date.ToString("yyyy-MM-dd");
        var granted = await context.FiniteTransactions.Include(t => t.Product)
            .Where(t => t.User.Id == subscription.User.Id && t.Reference == reference + "-topup").FirstOrDefaultAsync();
        var plan = granted == null ? null : await context.TopUpProducts.Include(p => p.Groups).FirstOrDefaultAsync(p => p.Id == granted.ProductId);
        if (plan == null)
        {
            logger.LogWarning("Won dispute of payment {PaymentId}: its grant {Reference} was not found, nothing restored", payment.Id, reference);
            return;
        }
        await GrantPaidPeriod(subscription.User.ExternalId, plan, await AccessHolder(subscription), DisputeWonReference(subscription, payment));
    }

    private async Task<bool> RestorePlanChange(UserSubscription subscription, string invoiceId)
    {
        var change = await context.SubscriptionPlanChanges.Include(c => c.Product)
            .SingleOrDefaultAsync(c => c.InvoiceId == invoiceId && c.SubscriptionId == subscription.ExternalId);
        if (change == null)
            return false;
        change.Refunded = false;
        if (await AccessExpiry(subscription) <= change.PeriodEnd.AddSeconds(5))
            await SetAccessProduct(subscription, change.Product);
        return true;
    }

    /// <summary>
    /// Ends the subscription now (no further charges), also what was booked after it: Stripe's renewal schedule is released first
    /// (Stripe refuses to cancel a schedule-managed subscription), PayPal's continuation is cancelled. Does nothing for an ended one.
    /// </summary>
    private async Task CancelImmediately(UserSubscription subscription)
    {
        if (subscription.Status == "expired")
            return;
        await CancelBookedExtension(subscription);
        if (subscription.ProviderSlug == SubscriptionProviders.Stripe)
            await CancelStripeNow(subscription);
        else if (subscription.ProviderSlug == SubscriptionProviders.PayPal)
            await CancelPayPalNow(subscription);
        subscription.Status = "expired";
        subscription.EndsAt = DateTime.UtcNow;
        subscription.UpdatedAt = DateTime.UtcNow;
    }

    private async Task CancelStripeNow(UserSubscription subscription)
    {
        var current = await StripeApi.GetSubscription(subscription.ExternalId);
        if (current.Status != "canceled" && MayHaveRenewalSchedule(subscription))
            current = await ReleaseStripeRenewal(subscription, current);
        ApplyStripeState(subscription, current.Status == "canceled" ? current : await StripeApi.CancelNow(subscription.ExternalId));
    }

    private async Task CancelPayPalNow(UserSubscription subscription)
    {
        var remote = await PayPalApi.Get(subscription.ExternalId);
        if (remote?.Status is "CANCELLED" or "EXPIRED")
            return;
        await PayPalApi.Cancel(subscription.ExternalId, "Payment disputed");
    }
}
