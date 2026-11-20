using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.PayPal;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// PayPal term switch (<see cref="SwitchTerm"/>): PayPal can not change a cycle's length, so the new term is a new PayPal
/// subscription of the long plan starting now (TRIAL = the price charged now, REGULAR = the 4-week renewal price), like the
/// continuation of an extension. Nothing changes until the payer approved it; on activation it is linked to the old subscription
/// (<see cref="UserSubscription.PreviousSubscriptionId"/>), which is cancelled at PayPal, and its first sale takes over the old
/// subscription's access (ownership or tier slots) and extends it, so the paid days left are kept.
/// </summary>
public partial class SubscriptionService
{
    private const string TermSwitchCancelReason = "Replaced by the longer term the customer switched to";
    private const string TermSwitchAbandonedReason = "The switch to a longer term was replaced or not approved in time";

    private async Task<SubscriptionTermSwitchResult> SwitchPayPalTerm(UserSubscription subscription, TermSwitchQuote quote,
        SubscriptionTermSwitchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReturnUrl) || string.IsNullOrWhiteSpace(request.CancelUrl))
            throw new ApiException("A PayPal term switch needs a return and a cancel url.");
        var remote = await PayPalApi.Get(subscription.ExternalId)
            ?? throw new ApiException("PayPal did not return the subscription. Please try again later.");
        await ApplyPayPalState(subscription, remote);
        await context.SaveChangesAsync();
        if (subscription.Status != "active" || subscription.EndsAt != null)
            throw new ApiException("subscription_not_active");
        var row = await StartTermSwitch(subscription, quote, request.Declaration);
        PaymentRequest checkout = null;
        try
        {
            checkout = await PayPalTermSwitchCheckout(subscription, quote, row, request.Declaration);
            await OpenPayPalTermSubscription(subscription, quote, checkout, row, request);
        }
        catch (Exception)
        {
            await FailPayPalTermSwitch(row, checkout);
            throw;
        }
        return TermSwitchResult(subscription, row, "approval_required", quote.FirstRenewalAt);
    }

    private async Task OpenPayPalTermSubscription(UserSubscription subscription, TermSwitchQuote quote, PaymentRequest checkout,
        SubscriptionTermSwitch row, SubscriptionTermSwitchRequest request)
    {
        var created = await CreatePayPalTermSubscription(subscription, quote, checkout, request);
        checkout.SessionId = created.Id;
        (row.NewSubscriptionId, row.PaymentUrl) = (created.Id, ApprovalLink(created));
        await context.SaveChangesAsync();
        logger.LogInformation("PayPal subscription {NewSubscriptionId} awaits approval to switch {SubscriptionId} to {Plan} at {Cents} cents",
            created.Id, subscription.ExternalId, quote.Plan.Slug, quote.ChargedCents);
    }

    /// <summary>Nothing reached the payer: the switch failed (its promotion use is given back) and so did its checkout record</summary>
    private async Task FailPayPalTermSwitch(SubscriptionTermSwitch row, PaymentRequest checkout)
    {
        if (checkout != null)
            (checkout.State, checkout.UpdatedAt) = (PaymentRequest.Status.FAILED, DateTime.UtcNow);
        await FailTermSwitch(row);
    }

    /// <summary>
    /// The record the new PayPal subscription is activated from (<see cref="EnsurePayPalSubscription"/>): the price charged now, then
    /// the renewal price, for the subscription's checkout country, with its codes (the promotion only lowers the first price). Marked
    /// with <see cref="PaymentRequest.ExtendsSubscriptionId"/>, so it is never taken for an open checkout; a promotion price must be
    /// approved before the promotion expired (plus the usual grace, <see cref="ApprovedAfterCodeExpired"/>).
    /// </summary>
    private async Task<PaymentRequest> PayPalTermSwitchCheckout(UserSubscription subscription, TermSwitchQuote quote,
        SubscriptionTermSwitch row, SubscriptionDeclaration declaration)
    {
        var now = DateTime.UtcNow;
        var request = new PaymentRequest
        {
            User = subscription.User, ProductId = quote.Plan, Provider = SubscriptionProviders.PayPal,
            Amount = StripeAmounts.FromMinorUnits(quote.ChargedCents, subscription.Currency),
            RecurringAmount = StripeAmounts.FromMinorUnits(quote.RenewalCents, subscription.Currency),
            Country = subscription.CheckoutCountry, CheckoutRegion = subscription.CheckoutRegion,
            CreatorCode = subscription.CreatorCode, DiscountCode = subscription.DiscountCode, CoinAmount = quote.Plan.Cost,
            ExpiresAt = quote.Promotion?.ExpiresAt, ExtendsSubscriptionId = subscription.ExternalId,
            State = PaymentRequest.Status.CREATED, CreatedAt = now, UpdatedAt = now
        };
        context.PaymentRequests.Add(request);
        await context.SaveChangesAsync();
        // The declaration names the checkout the new subscription is activated from (its first sale's confirmation reads it there)
        row.PaymentRequestId = request.Id;
        await ExtensionDeclarations.RecordExtension(declaration, subscription.User, quote.Plan, TermSwitchReference(row), request.Id, quote.Now);
        return request;
    }

    private async Task<PayPalSubscription> CreatePayPalTermSubscription(UserSubscription subscription, TermSwitchQuote quote,
        PaymentRequest checkout, SubscriptionTermSwitchRequest request)
    {
        var planId = await PayPalPlans.GetOrCreate(quote.Plan, quote.RenewalPlan);
        var created = await PayPalApi.Create(new PayPalSubscriptionRequest(planId,
            PayPalCustomId(quote.Plan.Id, subscription.User.ExternalId), checkout.Amount, subscription.Currency,
            request.ReturnUrl, request.CancelUrl, null, checkout.RecurringAmount));
        return ApprovalLink(created) != null ? created : throw new ApiException("PayPal did not return an approval link.");
    }

    /// <summary>
    /// Gives up a pending switch's new PayPal subscription the payer did not approve: true when it can be dropped (not approved; it
    /// is cancelled at PayPal, best effort, so its link can no longer start a subscription), false when the payer approved it
    /// (its activation links it)
    /// </summary>
    private async Task<bool> AbandonPayPalTermSwitch(string newSubscriptionId)
    {
        var status = await PayPalSwitchStatus(newSubscriptionId);
        if (IsApprovedAtPayPal(status))
            return false;
        await TryCancelUnapprovedPayPal(newSubscriptionId, status);
        return true;
    }

    /// <summary>PayPal's status of a switch's new subscription, null without one</summary>
    private async Task<string> PayPalSwitchStatus(string newSubscriptionId) =>
        newSubscriptionId == null ? null : (await PayPalApi.Get(newSubscriptionId))?.Status;

    private static bool IsApprovedAtPayPal(string status) => status is "APPROVED" or "ACTIVE" or "SUSPENDED";

    /// <summary>
    /// A PayPal extension of the current term waits for the payer's approval or was approved (both would bill). One for an earlier
    /// term end, cancelled or unknown to PayPal does not count, so an abandoned extension never blocks for longer than its term.
    /// When PayPal can not be read it counts (the safe side).
    /// </summary>
    private async Task<bool> HasLivePayPalExtension(UserSubscription subscription, DateTime now)
    {
        if (subscription.ExtensionSubscriptionId == null || subscription.RenewsAt <= now || paypal == null)
            return false;
        try
        {
            var remote = await PayPalApi.Get(subscription.ExtensionSubscriptionId);
            return remote?.Status is "APPROVAL_PENDING" or "APPROVED" or "ACTIVE" or "SUSPENDED" && StartsWhereItEnds(subscription, remote);
        }
        catch (Exception e) when (IsPayPalNotFound(e))
        {
            return false;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The PayPal extension {ExtensionId} of {SubscriptionId} could not be read; treating it as live",
                subscription.ExtensionSubscriptionId, subscription.ExternalId);
            return true;
        }
    }

    private async Task TryCancelUnapprovedPayPal(string newSubscriptionId, string status)
    {
        if (status is null or "CANCELLED" or "EXPIRED")
            return;
        try
        {
            await PayPalApi.Cancel(newSubscriptionId, TermSwitchAbandonedReason);
        }
        catch (Exception e)
        {
            // PayPal may refuse to cancel a subscription waiting for approval; an approval of it later is refused on activation
            logger.LogWarning(e, "Unapproved PayPal subscription {NewSubscriptionId} of an abandoned term switch could not be cancelled", newSubscriptionId);
        }
    }

    /// <summary>
    /// Webhooks of a PayPal subscription: when it is the new subscription of a term switch it is linked to the old one (true), refused
    /// (false: its sales are marked for a manual refund) when the old one was switched by another approved switch meanwhile, and the
    /// old one is cancelled at PayPal (its paid term stays; retried until it is). Null when it is no term switch. Idempotent.
    /// </summary>
    private async Task<bool?> LinkPayPalTermSwitch(UserSubscription continuation)
    {
        var row = await context.SubscriptionTermSwitches
            .FirstOrDefaultAsync(r => r.Provider == SubscriptionProviders.PayPal && r.NewSubscriptionId == continuation.ExternalId);
        if (row == null)
            return null;
        var previous = await FindProviderSubscription(SubscriptionProviders.PayPal, row.SubscriptionId);
        if (continuation.PreviousSubscriptionId == null && !await AcceptPayPalTermSwitch(continuation, previous, row))
            return continuation.ContinuationRefusedAt == null;
        if (previous?.Status is "active" or "paused")
            await WithSubscriptionLock(previous.ExternalId, () => CancelContinuedPayPal(previous, TermSwitchCancelReason));
        return true;
    }

    /// <summary>
    /// Links the new subscription (true); refuses it (false, flagged; <see cref="TermSwitchRefusal"/>); leaves one the payer cancelled
    /// before it was linked unlinked (false, not flagged)
    /// </summary>
    private async Task<bool> AcceptPayPalTermSwitch(UserSubscription continuation, UserSubscription previous, SubscriptionTermSwitch row)
    {
        if (await TermSwitchRefusal(continuation, previous, row) is string refusal)
        {
            await RefusePayPalTermSwitch(continuation, row, refusal);
            return false;
        }
        if (continuation.Status is "cancelled" or "expired")
            return false;
        // An expired (unapproved in time, but approved after all) switch is accepted, a replaced or refused one is not. A completed one
        // is this continuation's own whose link was not saved (crash, failed save): linked again.
        if (row.Status != TermSwitchStatus.Completed && !await CompleteTermSwitch(row, TermSwitchStatus.Expired))
        {
            await RefusePayPalTermSwitch(continuation, row, $"the switch is {row.Status}");
            return false;
        }
        continuation.PreviousSubscriptionId = previous.ExternalId;
        await context.SaveChangesAsync();
        logger.LogInformation("PayPal subscription {NewSubscriptionId} replaces {SubscriptionId} with a {Plan} term",
            continuation.ExternalId, previous.ExternalId, continuation.Product.Slug);
        return true;
    }

    /// <summary>
    /// Why an approved switch can not replace the old subscription, null when it can: the switch was replaced or refused, the old
    /// subscription is someone else's, no longer active, has a live PayPal extension, or another subscription continues it already
    /// </summary>
    private async Task<string> TermSwitchRefusal(UserSubscription continuation, UserSubscription previous, SubscriptionTermSwitch row)
    {
        if (row.Status is not (TermSwitchStatus.Pending or TermSwitchStatus.Expired or TermSwitchStatus.Completed))
            return $"the switch is {row.Status}";
        if (previous == null || previous.User.Id != continuation.User.Id)
            return "the old subscription is not the buyer's";
        if (previous.Status != "active" || previous.EndsAt != null)
            return $"the old subscription is {previous.Status}";
        if (await HasLivePayPalExtension(previous, DateTime.UtcNow))
            return "the old subscription has a PayPal extension";
        var continued = await context.Subscriptions.AnyAsync(s => s.ProviderSlug == SubscriptionProviders.PayPal
            && s.PreviousSubscriptionId == previous.ExternalId && s.ExternalId != continuation.ExternalId);
        return continued ? "another subscription continues the old one" : null;
    }

    /// <summary>Saved before the cancellation at PayPal; a failed cancellation is retried by the hourly run</summary>
    private async Task RefusePayPalTermSwitch(UserSubscription continuation, SubscriptionTermSwitch row, string refusal)
    {
        logger.LogWarning("PayPal subscription {NewSubscriptionId} was approved to switch {SubscriptionId}, but {Refusal}; refusing it (its sales need a manual refund)",
            continuation.ExternalId, row.SubscriptionId, refusal);
        if (!await EndPendingTermSwitch(row, TermSwitchStatus.Failed))
            await ChangeTermSwitchStatus(row, TermSwitchStatus.Failed, TermSwitchStatus.Expired);
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
            logger.LogError(e, "Refused PayPal term switch {NewSubscriptionId} could not be cancelled; the hourly run retries it", continuation.ExternalId);
        }
    }

    /// <summary>
    /// The first sale of a switch's new subscription extends the access the old subscription paid for: the old ownership moves to
    /// the new subscription (tier slots move with <see cref="AdoptPreviousSlots"/>), so the new term is added on top of it
    /// </summary>
    private async Task AdoptSwitchedAccess(UserSubscription subscription)
    {
        if (subscription.PreviousSubscriptionId == null || subscription.Product.SlotCount > 0 || await IsTermPaid(subscription)
            || !await IsPayPalTermSwitch(subscription))
            return;
        var previousId = subscription.PreviousSubscriptionId;
        var access = await context.OwnerShips
            .FirstOrDefaultAsync(o => o.UserId == subscription.User.Id && o.SubscriptionId == previousId);
        if (access == null)
            return;
        access.SubscriptionId = subscription.ExternalId;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The subscription whose access a payment of this one extends: itself, or, once a PayPal term switch moved its access (ownership
    /// or tier slots) to the new subscription, that one. A sale of the old subscription after the switch (charged before PayPal
    /// processed its cancellation) then adds its period on top of the new term, as its refund takes it from there
    /// (<see cref="TransactionService"/> follows the switch the same way); its reference is unique, so it is granted once.
    /// </summary>
    private async Task<string> AccessHolder(UserSubscription subscription)
    {
        if (!IsPayPal(subscription) || await HoldsAccess(subscription))
            return subscription.ExternalId;
        var successor = await context.SubscriptionTermSwitches
            .Where(r => r.Provider == SubscriptionProviders.PayPal && r.SubscriptionId == subscription.ExternalId
                && r.Status == TermSwitchStatus.Completed && r.NewSubscriptionId != null)
            .Select(r => r.NewSubscriptionId).FirstOrDefaultAsync();
        return successor ?? subscription.ExternalId;
    }

    private Task<bool> HoldsAccess(UserSubscription subscription)
    {
        var userId = subscription.User.Id;
        var id = subscription.ExternalId;
        return subscription.Product.SlotCount > 0
            ? context.TierSlots.AnyAsync(t => t.UserId == userId && t.SubscriptionId == id)
            : context.OwnerShips.AnyAsync(o => o.UserId == userId && o.SubscriptionId == id);
    }

    private Task<bool> IsPayPalTermSwitch(UserSubscription subscription) => context.SubscriptionTermSwitches.AnyAsync(r =>
        r.Provider == SubscriptionProviders.PayPal && r.NewSubscriptionId == subscription.ExternalId && r.Status == TermSwitchStatus.Completed);

    /// <summary>The term switch the first sale of a new PayPal subscription pays, null when it is none</summary>
    private async Task<SubscriptionTermSwitchInfo> PayPalTermSwitchInfo(UserSubscription subscription, PayPalSale sale)
    {
        var row = await context.SubscriptionTermSwitches.Include(r => r.PreviousProduct).Include(r => r.Product).AsNoTracking()
            .FirstOrDefaultAsync(r => r.Provider == SubscriptionProviders.PayPal && r.NewSubscriptionId == subscription.ExternalId
                && r.Status == TermSwitchStatus.Completed);
        return row == null ? null : TermSwitchInfo(row, ParseTime(sale.CreateTime) ?? DateTime.UtcNow, await AccessExpiry(subscription));
    }

    /// <summary>
    /// Hourly (renewal conversion run): a switch approved at PayPal whose activation webhook was lost would leave the old subscription
    /// billing next to the new one. Pending switches are checked and linked like their activation webhook, and linked ones whose old
    /// subscription is still active (its cancellation failed) are retried the same way.
    /// </summary>
    private async Task LinkApprovedPayPalTermSwitches(CancellationToken cancellation)
    {
        if (paypal == null)
            return;
        var open = await context.SubscriptionTermSwitches.AsNoTracking()
            .Where(r => r.Provider == SubscriptionProviders.PayPal && r.NewSubscriptionId != null
                && (r.Status == TermSwitchStatus.Pending || r.Status == TermSwitchStatus.Completed
                    && context.Subscriptions.Any(s => s.ProviderSlug == SubscriptionProviders.PayPal && s.ExternalId == r.SubscriptionId
                        && s.Status == "active")))
            .Select(r => r.NewSubscriptionId).ToListAsync(cancellation);
        foreach (var newSubscriptionId in open)
        {
            cancellation.ThrowIfCancellationRequested();
            await TryLinkApprovedPayPalExtension(newSubscriptionId);
        }
    }
}
