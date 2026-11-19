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
/// PayPal can not change a subscription's next cycle without the payer, so one more term (<see cref="ExtendLongTerm"/>) is a new
/// PayPal subscription on the same plan that starts when the current term ends and, once approved, replaces the current one.
/// </summary>
public partial class SubscriptionService
{
    private const string ContinuedCancelReason = "Continued by the extension the customer approved";
    private const string RefusedContinuationReason = "The subscription it would extend can no longer be extended";
    /// <summary>How far the continuation's start may lie from the term end it was made for (PayPal normalizes start_time)</summary>
    private static readonly TimeSpan ContinuationStartTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// The new subscription's TRIAL cycle (the long term) costs the extension price and starts when the current term ends,
    /// followed by 4-week cycles at the renewal price. A pending one (not approved yet) is reused, so a payer can approve only one.
    /// </summary>
    private async Task<SubscriptionExtensionResult> ExtendPayPal(UserSubscription subscription, long cents, SubscriptionExtensionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReturnUrl) || string.IsNullOrWhiteSpace(request.CancelUrl))
            throw new ApiException("A PayPal extension needs a return and a cancel url.");
        // PayPal is authoritative for the term end (a missed webhook leaves RenewsAt behind)
        var remote = await PayPalApi.Get(subscription.ExternalId)
            ?? throw new ApiException("PayPal did not return the subscription. Please try again later.");
        await ApplyPayPalState(subscription, remote);
        await context.SaveChangesAsync();
        if (!MayExtendLongTerm(subscription, DateTime.UtcNow, extensionBookingWindow))
            throw new ApiException("This subscription can not be extended for another term.");
        var termEnd = subscription.RenewsAt;
        var reference = SubscriptionDeclarationService.ExtensionReference(subscription.ExternalId, termEnd);
        var plan = (TopUpProduct)subscription.Product;
        var pending = await PendingPayPalExtension(subscription);
        if (pending != null)
        {
            await ExtensionDeclarations.RecordExtension(request.Declaration, subscription.User, plan, reference, pending.Value.requestId, termEnd);
            return ExtensionResult(subscription, termEnd, pending.Value.approvalUrl);
        }
        var checkout = await PayPalExtensionCheckout(subscription, cents);
        await ExtensionDeclarations.RecordExtension(request.Declaration, subscription.User, plan, reference, checkout.Id, termEnd);
        var created = await CreatePayPalContinuation(subscription, checkout, request, termEnd);
        checkout.SessionId = created.Id;
        subscription.ExtensionSubscriptionId = created.Id;
        subscription.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        logger.LogInformation("PayPal subscription {ContinuationId} awaits approval to extend {SubscriptionId} by another {Plan} term at {Cents} cents from {TermEnd}",
            created.Id, subscription.ExternalId, plan.Slug, cents, termEnd);
        return ExtensionResult(subscription, termEnd, ApprovalLink(created));
    }

    /// <summary>
    /// The approval link of the extension created before, if the payer can still approve it; refuses a second extension when the
    /// first one was approved already
    /// </summary>
    private async Task<(int? requestId, string approvalUrl)?> PendingPayPalExtension(UserSubscription subscription)
    {
        var id = subscription.ExtensionSubscriptionId;
        if (id == null)
            return null;
        var remote = await PayPalApi.Get(id);
        if (remote?.Status is "APPROVED" or "ACTIVE" or "SUSPENDED")
            throw new ApiException("This subscription is already extended.");
        var approve = remote?.Status == "APPROVAL_PENDING" ? ApprovalLink(remote) : null;
        if (approve == null)
            return null;
        var requestId = await context.PaymentRequests.Where(r => r.Provider == SubscriptionProviders.PayPal && r.SessionId == id)
            .OrderByDescending(r => r.Id).Select(r => (int?)r.Id).FirstOrDefaultAsync();
        return (requestId, approve);
    }

    /// <summary>
    /// The record the new PayPal subscription is activated from (<see cref="EnsurePayPalSubscription"/>): the extension price first,
    /// then the renewal price, priced for the subscription's checkout country, with its codes. Marked as an extension
    /// (<see cref="PaymentRequest.ExtendsSubscriptionId"/>), so it is never taken for an open checkout.
    /// </summary>
    private async Task<PaymentRequest> PayPalExtensionCheckout(UserSubscription subscription, long cents)
    {
        var plan = (TopUpProduct)subscription.Product;
        var now = DateTime.UtcNow;
        var request = new PaymentRequest
        {
            User = subscription.User, ProductId = plan, Provider = SubscriptionProviders.PayPal,
            Amount = StripeAmounts.FromMinorUnits(cents, subscription.Currency),
            RecurringAmount = StripeAmounts.FromMinorUnits(subscription.RenewalPriceCents.Value, subscription.Currency),
            Country = subscription.CheckoutCountry, CheckoutRegion = subscription.CheckoutRegion,
            CreatorCode = subscription.CreatorCode, DiscountCode = subscription.DiscountCode, CoinAmount = plan.Cost,
            ExtendsSubscriptionId = subscription.ExternalId, State = PaymentRequest.Status.CREATED, CreatedAt = now, UpdatedAt = now
        };
        context.PaymentRequests.Add(request);
        await context.SaveChangesAsync();
        return request;
    }

    private async Task<PayPalSubscription> CreatePayPalContinuation(UserSubscription subscription, PaymentRequest checkout,
        SubscriptionExtensionRequest request, DateTime termEnd)
    {
        try
        {
            var plan = (TopUpProduct)subscription.Product;
            var planId = await PayPalPlans.GetOrCreate(plan, (TopUpProduct)subscription.RenewalProduct);
            var created = await PayPalApi.Create(new PayPalSubscriptionRequest(planId,
                PayPalCustomId(plan.Id, subscription.User.ExternalId), checkout.Amount, subscription.Currency,
                request.ReturnUrl, request.CancelUrl, null, checkout.RecurringAmount, termEnd));
            return ApprovalLink(created) != null ? created : throw new ApiException("PayPal did not return an approval link.");
        }
        catch (Exception)
        {
            checkout.State = PaymentRequest.Status.FAILED;
            checkout.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            throw;
        }
    }

    private static string ApprovalLink(PayPalSubscription subscription) =>
        subscription?.Links?.FirstOrDefault(l => l.Rel == "approve")?.Href;

    /// <summary>
    /// A PayPal subscription the payer approved as the extension of another one replaces it: it is linked as its continuation and
    /// the previous subscription is cancelled at PayPal (its paid term keeps running; nothing is revoked). The continuation is a
    /// 13 or 52 week term continuing every 4 weeks like any other. One whose previous subscription can no longer be extended is
    /// refused for good (<see cref="UserSubscription.ContinuationRefusedAt"/>; returns false: its payments are not granted).
    /// Idempotent; webhooks of the continuation call it until the previous one is cancelled.
    /// </summary>
    private async Task<bool> LinkPayPalContinuation(UserSubscription continuation, PayPalSubscription remote)
    {
        if (continuation.ContinuationRefusedAt != null)
            return false;
        var previous = await PreviousSubscription(continuation);
        if (previous == null)
            return true;
        if (continuation.PreviousSubscriptionId == null && !await AcceptPayPalContinuation(continuation, previous, remote))
            return continuation.ContinuationRefusedAt == null;
        if (continuation.PreviousSubscriptionId != null && previous.Status == "active")
            await WithSubscriptionLock(previous.ExternalId, () => CancelContinuedPayPal(previous));
        return true;
    }

    private Task<UserSubscription> PreviousSubscription(UserSubscription continuation)
    {
        if (continuation.PreviousSubscriptionId != null)
            return FindProviderSubscription(SubscriptionProviders.PayPal, continuation.PreviousSubscriptionId);
        return context.Subscriptions.Include(s => s.User).Include(s => s.Product).ThenInclude(p => p.Groups)
            .Include(s => s.RenewalProduct).ThenInclude(p => p.Groups)
            .FirstOrDefaultAsync(s => s.ProviderSlug == SubscriptionProviders.PayPal && s.ExtensionSubscriptionId == continuation.ExternalId);
    }

    /// <summary>
    /// Links the continuation (true), refuses it (false, flagged), or leaves one the payer cancelled before it was linked
    /// unlinked (false, not flagged: it has nothing to continue)
    /// </summary>
    private async Task<bool> AcceptPayPalContinuation(UserSubscription continuation, UserSubscription previous, PayPalSubscription remote)
    {
        if (!ContinuesWhereItEnds(previous, continuation, remote))
        {
            await RefusePayPalContinuation(continuation, previous);
            return false;
        }
        if (continuation.Status is "cancelled" or "expired")
            return false;
        previous.ExtensionStartsAt = previous.RenewsAt;
        previous.ExtensionPriceCents = continuation.PriceCents;
        previous.UpdatedAt = DateTime.UtcNow;
        continuation.PreviousSubscriptionId = previous.ExternalId;
        await context.SaveChangesAsync();
        logger.LogInformation("PayPal subscription {ContinuationId} continues {SubscriptionId} for another {Plan} term from {TermEnd}",
            continuation.ExternalId, previous.ExternalId, continuation.Product.Slug, previous.RenewsAt);
        return true;
    }

    /// <summary>
    /// The previous subscription is still active, of the same buyer and in the long term the extension was made for (not
    /// converted or billed past it), which ends at the continuation's start and not within <see cref="ExtensionCutoff"/>
    /// </summary>
    private static bool ContinuesWhereItEnds(UserSubscription previous, UserSubscription continuation, PayPalSubscription remote) =>
        previous.Status == "active" && previous.EndsAt == null && InFirstLongTerm(previous)
        && previous.RenewsAt > DateTime.UtcNow + ExtensionCutoff && previous.User.Id == continuation.User.Id
        && StartsWhereItEnds(previous, remote);

    private static bool StartsWhereItEnds(UserSubscription previous, PayPalSubscription remote) =>
        ParseTime(remote?.StartTime) is not DateTime start || (start - previous.RenewsAt).Duration() <= ContinuationStartTolerance;

    /// <summary>
    /// The refusal is saved first; a failed cancellation at PayPal is logged and retried by the hourly run
    /// (<see cref="RetryRefusedContinuationCancels"/>)
    /// </summary>
    private async Task RefusePayPalContinuation(UserSubscription continuation, UserSubscription previous)
    {
        logger.LogWarning("PayPal subscription {ContinuationId} was approved as the extension of {SubscriptionId} ({Status}), which can no longer be extended; refusing it",
            continuation.ExternalId, previous.ExternalId, previous.Status);
        continuation.ContinuationRefusedAt = DateTime.UtcNow;
        continuation.EndsAt = continuation.RenewsAt;
        continuation.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        try
        {
            await CancelRefusedContinuation(continuation);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Refused PayPal extension {ContinuationId} could not be cancelled; the hourly run retries it", continuation.ExternalId);
        }
    }

    /// <summary>Cancels a refused continuation at PayPal (if it still runs there) and marks it cancelled</summary>
    private async Task<bool> CancelRefusedContinuation(UserSubscription continuation)
    {
        var remote = await PayPalApi.Get(continuation.ExternalId);
        if (remote?.Status is "ACTIVE" or "SUSPENDED")
            await PayPalApi.Cancel(continuation.ExternalId, RefusedContinuationReason);
        continuation.Status = "cancelled";
        continuation.EndsAt ??= continuation.RenewsAt;
        continuation.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>Hourly: refused continuations not known as cancelled yet are cancelled at PayPal again (a failed cancellation)</summary>
    private async Task RetryRefusedContinuationCancels(System.Threading.CancellationToken cancellation)
    {
        var refused = await context.Subscriptions
            .Where(s => s.ProviderSlug == SubscriptionProviders.PayPal && s.ContinuationRefusedAt != null
                && s.Status != "cancelled" && s.Status != "expired")
            .Select(s => s.ExternalId).ToListAsync(cancellation);
        foreach (var id in refused)
        {
            cancellation.ThrowIfCancellationRequested();
            await TryCancelRefusedContinuation(id);
        }
    }

    private async Task TryCancelRefusedContinuation(string continuationId)
    {
        try
        {
            await WithSubscriptionLock(continuationId, async () =>
                await CancelRefusedContinuation(await FindProviderSubscription(SubscriptionProviders.PayPal, continuationId)));
            logger.LogInformation("Cancelled the refused PayPal extension {ContinuationId}", continuationId);
        }
        catch (SubscriptionBusyException)
        {
            logger.LogDebug("Refused PayPal extension {ContinuationId} is being changed elsewhere; retried by the next run", continuationId);
        }
        catch (Exception e)
        {
            context.ChangeTracker.Clear();
            logger.LogError(e, "Refused PayPal extension {ContinuationId} could not be cancelled; retried by the next run", continuationId);
        }
    }

    /// <summary>The continued subscription stops billing; its paid term (and the access granted for it) runs out as it is</summary>
    private async Task<bool> CancelContinuedPayPal(UserSubscription previous)
    {
        var remote = await PayPalApi.Get(previous.ExternalId);
        if (remote?.Status is "ACTIVE" or "SUSPENDED")
            await PayPalApi.Cancel(previous.ExternalId, ContinuedCancelReason);
        previous.Status = "cancelled";
        previous.EndsAt = await AccessExpiry(previous) ?? previous.RenewsAt;
        previous.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Hourly (renewal conversion run): an approved PayPal extension whose activation webhook was lost would leave the old
    /// subscription billing its 4-week renewal next to it. Pending extensions of active subscriptions whose term has not ended are
    /// checked at PayPal; an active one is handled like its activation webhook (linked, the old subscription cancelled). A linked
    /// one whose old subscription is still active (its cancellation failed) is retried the same way.
    /// </summary>
    private async Task LinkApprovedPayPalExtensions(System.Threading.CancellationToken cancellation)
    {
        if (paypal == null)
            return;
        var now = DateTime.UtcNow;
        // a refused continuation is never linked; its cancellation is retried below instead
        var pending = await context.Subscriptions.AsNoTracking()
            .Where(s => s.ProviderSlug == SubscriptionProviders.PayPal && s.ExtensionSubscriptionId != null && s.Status == "active"
                && s.EndsAt == null && s.RenewsAt > now
                && !context.Subscriptions.Any(c => c.ProviderSlug == SubscriptionProviders.PayPal
                    && c.ExternalId == s.ExtensionSubscriptionId && c.ContinuationRefusedAt != null))
            .Select(s => s.ExtensionSubscriptionId).ToListAsync(cancellation);
        foreach (var continuationId in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            await TryLinkApprovedPayPalExtension(continuationId);
        }
        await RetryRefusedContinuationCancels(cancellation);
    }

    private async Task TryLinkApprovedPayPalExtension(string continuationId)
    {
        try
        {
            var remote = await PayPalApi.Get(continuationId);
            if (remote?.Status is not ("ACTIVE" or "SUSPENDED"))
                return;
            logger.LogWarning("PayPal extension {ContinuationId} is active but was not linked (activation webhook missed); linking it now", continuationId);
            var outcomes = await PayPalSubscriptionChanged(continuationId);
            if (outcomes.Count > 0)
                logger.LogWarning("PayPal extension {ContinuationId} had {Count} granted sales; their PAYMENT.SALE.COMPLETED webhooks record them",
                    continuationId, outcomes.Count);
        }
        catch (SubscriptionBusyException)
        {
            logger.LogDebug("PayPal extension {ContinuationId} is being changed elsewhere; checked again by the next run", continuationId);
        }
        catch (Exception e)
        {
            context.ChangeTracker.Clear();
            logger.LogError(e, "PayPal extension {ContinuationId} could not be checked; retried by the next run", continuationId);
        }
    }

    /// <summary>Whether a PayPal continuation exists and still runs (not cancelled in PayPal or by us)</summary>
    private Task<bool> IsActiveContinuation(string continuationId) => context.Subscriptions.AnyAsync(s =>
        s.ProviderSlug == SubscriptionProviders.PayPal && s.ExternalId == continuationId && s.Status == "active" && s.EndsAt == null);

    /// <summary>
    /// A slot bundle's tier slots (and their assignments) move from the replaced PayPal subscription to its continuation with the
    /// continuation's first payment, which then extends them
    /// </summary>
    private async Task AdoptPreviousSlots(UserSubscription subscription)
    {
        if (subscription.PreviousSubscriptionId == null || subscription.Product.SlotCount == 0 || await IsTermPaid(subscription))
            return;
        var previousId = subscription.PreviousSubscriptionId;
        await context.TierSlots.Where(s => s.UserId == subscription.User.Id && s.SubscriptionId == previousId)
            .ExecuteUpdateAsync(s => s.SetProperty(slot => slot.SubscriptionId, subscription.ExternalId));
    }

    private async Task CancelPayPalContinuation(string continuationId)
    {
        var remote = await PayPalApi.Get(continuationId);
        if (remote?.Status is not ("ACTIVE" or "SUSPENDED"))
            return;
        await PayPalApi.Cancel(continuationId, PayPalCancelReason);
        var continuation = await FindProviderSubscription(SubscriptionProviders.PayPal, continuationId);
        if (continuation == null)
            return;
        continuation.Status = "cancelled";
        continuation.EndsAt = await AccessExpiry(continuation) ?? continuation.RenewsAt;
        continuation.UpdatedAt = DateTime.UtcNow;
    }
}
