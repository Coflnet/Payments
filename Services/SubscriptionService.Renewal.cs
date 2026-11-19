using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// 13 and 52 week subscriptions continue as the 4-week plan of the same tier after their first term
/// (<see cref="SubscriptionRenewal"/>). The subscription keeps its long plan until the first renewal is paid; that payment
/// converts it (<see cref="UserSubscription.Product"/> becomes <see cref="UserSubscription.RenewalProduct"/>, the agreed
/// price <see cref="UserSubscription.RenewalPriceCents"/>) and grants 4 weeks.
/// Stripe moves the subscription with a subscription schedule; PayPal plans have a TRIAL cycle of the long period
/// followed by a 4-week REGULAR cycle.
/// </summary>
public partial class SubscriptionService
{
    /// <summary>How many days before its renewal a scheduled conversion is checked again by <see cref="ConvertDueRenewals"/></summary>
    internal const int RenewalRecheckDays = 3;
    /// <summary>Legacy PayPal 13/52 week subscriptions renewing within this many days are reported</summary>
    internal const int LegacyPayPalWarningDays = 30;

    /// <summary>A 13 or 52 week subscription that has not continued as its 4-week plan yet</summary>
    internal static bool InFirstLongTerm(UserSubscription subscription) =>
        subscription.RenewalProduct != null && SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product);

    private static bool BillsRenewalPlan(UserSubscription subscription, string billedSlug) =>
        InFirstLongTerm(subscription) && billedSlug != null && billedSlug == subscription.RenewalProduct.Slug;

    /// <summary>Whether a period of this subscription was granted already (its first term is paid)</summary>
    private Task<bool> HasGrantedPeriod(UserSubscription subscription) => context.FiniteTransactions
        .AnyAsync(t => t.SubscriptionId == subscription.ExternalId && t.User.Id == subscription.User.Id);

    /// <summary>
    /// The first renewal is paid: from now on the subscription is the 4-week plan at its renewal price. Not saved here: the grant
    /// of that renewal saves it in its own transaction (or the refund mark of a rejected payment), so a failure in between
    /// leaves the subscription unconverted and the provider's retry converts and grants it again.
    /// </summary>
    private void ConvertToRenewalPlan(UserSubscription subscription)
    {
        logger.LogInformation("Subscription {SubscriptionId} continues as {RenewalPlan} at {Cents} cents every 4 weeks after its first {Plan} term",
            subscription.ExternalId, subscription.RenewalProduct.Slug, subscription.RenewalPriceCents, subscription.Product.Slug);
        subscription.Product = subscription.RenewalProduct;
        subscription.PriceCents = subscription.RenewalPriceCents ?? subscription.PriceCents;
        subscription.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// A 13 or 52 week subscription recorded without renewal terms (sold before the 4-week renewal) gets them: the 4-week plan
    /// of its tier, priced like a plan switch (<see cref="RecurringPriceCents"/>: creator code, "forever" code with its
    /// 4-week amount, VAT of the checkout country). False when no renewal plan or price can be determined.
    /// </summary>
    private async Task<bool> EnsureRenewalTerms(UserSubscription subscription)
    {
        if (subscription.RenewalProduct != null && subscription.RenewalPriceCents != null)
            return true;
        var terms = await RenewalTermsOf(subscription, (TopUpProduct)subscription.Product);
        if (terms == null)
            return false;
        (subscription.RenewalProduct, subscription.RenewalPriceCents) = terms.Value;
        return true;
    }

    /// <summary>
    /// A plan switch during the first term: the subscription then continues as the 4-week plan of the new tier
    /// </summary>
    private async Task UpdateRenewalTerms(UserSubscription subscription, TopUpProduct target)
    {
        if (subscription.RenewalProduct == null || !SubscriptionRenewal.ContinuesAsFourWeeks(target))
            return;
        var terms = await RenewalTermsOf(subscription, target);
        if (terms != null)
            (subscription.RenewalProduct, subscription.RenewalPriceCents) = terms.Value;
    }

    private async Task<(Product plan, long? cents)?> RenewalTermsOf(UserSubscription subscription, TopUpProduct plan)
    {
        var renewal = await renewalPlans.Find(plan);
        var cents = renewal == null ? null : await RecurringPriceCents(subscription, renewal, subscription.CheckoutCountry);
        if (cents != null)
            return (renewal, cents);
        logger.LogError("Subscription {SubscriptionId} of {Plan} can not continue every 4 weeks: no 4-week plan ({RenewalPlan}) or no price for country {Country}",
            subscription.ExternalId, plan.Slug, renewal?.Slug, subscription.CheckoutCountry);
        return null;
    }

    /// <summary>
    /// Webhooks: schedules the conversion when it is due. Failures are logged and retried by <see cref="ConvertDueRenewals"/>;
    /// they never block recording a payment.
    /// </summary>
    private async Task TryScheduleStripeRenewal(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        try
        {
            await ScheduleStripeRenewal(subscription, snapshot);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Stripe subscription {SubscriptionId} could not be scheduled to continue every 4 weeks; the renewal conversion retries it",
                subscription.ExternalId);
        }
    }

    /// <summary>
    /// Attaches the schedule that moves a paid 13 or 52 week Stripe subscription to its 4-week renewal price when its term ends
    /// </summary>
    private async Task ScheduleStripeRenewal(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (!AwaitsStripeRenewalSchedule(subscription, snapshot) || !await HasGrantedPeriod(subscription))
            return;
        if (!await EnsureRenewalTerms(subscription))
            throw new ApiException($"Subscription {subscription.ExternalId} has no 4-week plan to continue as.");
        var request = await RenewalRequest(subscription, snapshot);
        subscription.RenewalScheduleId = await StripeApi.ScheduleRenewal(snapshot, request);
        subscription.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        logger.LogInformation("Stripe subscription {SubscriptionId} continues as {RenewalPlan} at {Cents} cents every 4 weeks from {TermEnd} (schedule {ScheduleId})",
            subscription.ExternalId, request.PlanSlug, request.UnitAmount, request.TermEnd, subscription.RenewalScheduleId);
    }

    /// <summary>
    /// A 13 or 52 week subscription that will renew (active, not cancelled, no pending upgrade), still bills its own plan
    /// and is not moved by our schedule yet
    /// </summary>
    internal static bool AwaitsStripeRenewalSchedule(UserSubscription subscription, StripeSubscriptionSnapshot snapshot) =>
        SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product)
        && (snapshot.Status is "active" or "past_due")
        && !snapshot.CancelAtPeriodEnd && snapshot.CancelAt == null && !snapshot.HasPendingUpdate
        && snapshot.CurrentPeriodEnd != null
        && StripePlans.SlugOf(snapshot) == subscription.Product.Slug
        && (snapshot.ScheduleId == null || snapshot.ScheduleId != subscription.RenewalScheduleId);

    private async Task<StripeRenewalRequest> RenewalRequest(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        var plan = (TopUpProduct)subscription.RenewalProduct;
        return new StripeRenewalRequest(subscription.RenewalPriceCents.Value, plan.CurrencyCode.ToLowerInvariant(), snapshot.TaxBehavior,
            plan.Slug, plan.Title, snapshot.CurrentPeriodEnd.Value, await RenewalTaxRate(subscription, snapshot),
            ExtensionTerm(subscription, snapshot));
    }

    /// <summary>Direct sales with VAT show the inclusive VAT rate on the receipts, as the checkout did</summary>
    private async Task<string> RenewalTaxRate(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (subscription.ManagedPayments)
            return null;
        var country = subscription.CheckoutCountry ?? MetadataCountry(snapshot);
        return tax?.RateFor(country) is int basisPoints && basisPoints > 0 ? await StripeApi.EnsureVatTaxRate(country, basisPoints) : null;
    }

    /// <summary>Whether the subscription may have a renewal schedule attached, which direct changes must release first</summary>
    private static bool MayHaveRenewalSchedule(UserSubscription subscription) =>
        subscription.RenewalScheduleId != null || subscription.RenewalProduct != null
        || SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product);

    /// <summary>
    /// Stripe refuses cancellation and price changes on a subscription a schedule manages, so the renewal schedule is released
    /// first (the subscription keeps its current period and price): our schedule (by stored id or by its marker, also after the
    /// conversion), and for a 13 or 52 week plan any schedule without changes planned by someone else (e.g. one an interrupted
    /// attempt left with only the current phase, whose id was never stored). A schedule someone else made with planned changes
    /// is left alone, as before.
    /// </summary>
    private async Task<StripeSubscriptionSnapshot> ReleaseStripeRenewal(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (!await IsReleasableRenewalSchedule(subscription, snapshot))
            return snapshot;
        var released = await StripeApi.ReleaseSchedule(snapshot.ScheduleId, snapshot.Id);
        logger.LogInformation("Released renewal schedule {ScheduleId} of Stripe subscription {SubscriptionId}", snapshot.ScheduleId, snapshot.Id);
        subscription.RenewalScheduleId = null;
        await context.SaveChangesAsync();
        return released;
    }

    private async Task<bool> IsReleasableRenewalSchedule(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (snapshot.ScheduleId == null)
            return false;
        if (snapshot.ScheduleId == subscription.RenewalScheduleId)
            return true;
        var schedule = await StripeApi.GetSchedule(snapshot.ScheduleId);
        if (schedule.IsRenewalSchedule)
            return true;
        return SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product) && !schedule.HasForeignPhases;
    }

    /// <summary>
    /// A resumed 13 or 52 week subscription must continue every 4 weeks; when that can not be scheduled the resume is undone
    /// (cancelled at period end again), so it never renews for another long term.
    /// </summary>
    private async Task ScheduleResumedStripeRenewal(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        try
        {
            await ScheduleStripeRenewal(subscription, snapshot);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Resumed Stripe subscription {SubscriptionId} could not be scheduled to continue every 4 weeks; cancelling it at period end again",
                subscription.ExternalId);
            await UndoResume(subscription);
            throw new ApiException("The subscription could not be resumed right now. Please try again later.");
        }
    }

    /// <summary>
    /// A schedule the failed attempt may have left attached would make Stripe refuse the cancellation: it is released first
    /// </summary>
    private async Task UndoResume(UserSubscription subscription)
    {
        try
        {
            await ReleaseStripeRenewal(subscription, await StripeApi.GetSubscription(subscription.ExternalId));
            ApplyStripeState(subscription, await StripeApi.SetCancelAtPeriodEnd(subscription.ExternalId, true));
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Resumed Stripe subscription {SubscriptionId} could neither be scheduled to continue every 4 weeks nor cancelled again; it may renew for its full period",
                subscription.ExternalId);
            throw;
        }
    }

    /// <summary>
    /// Converts the 13 and 52 week Stripe subscriptions that are not moved to their 4-week renewal yet (sold before it
    /// existed, or a webhook failed) and checks the scheduled ones again shortly before their renewal. Returns how many were
    /// scheduled. Also reports legacy PayPal subscriptions, which can not be moved without the payer, and links PayPal extensions
    /// whose activation webhook was lost (<see cref="LinkApprovedPayPalExtensions"/>).
    /// </summary>
    public async Task<int> ConvertDueRenewals(CancellationToken cancellation = default)
    {
        var recheckBefore = DateTime.UtcNow.AddDays(RenewalRecheckDays);
        var ids = await context.Subscriptions
            .Where(s => s.ProviderSlug == SubscriptionProviders.Stripe && (s.Status == "active" || s.Status == "past_due")
                && s.Product.OwnershipSeconds > SubscriptionRenewal.MaxFourWeekSeconds
                && (s.RenewalScheduleId == null || s.RenewsAt < recheckBefore))
            .Select(s => s.ExternalId).ToListAsync(cancellation);
        var scheduled = 0;
        foreach (var id in ids)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await TryConvertStripeRenewal(id))
                scheduled++;
        }
        await WarnAboutLegacyPayPalRenewals(cancellation);
        await LinkApprovedPayPalExtensions(cancellation);
        return scheduled;
    }

    private async Task<bool> TryConvertStripeRenewal(string subscriptionId)
    {
        try
        {
            return await WithSubscriptionLock(subscriptionId, () => ConvertStripeRenewal(subscriptionId));
        }
        catch (SubscriptionBusyException)
        {
            // Another replica's run or a webhook holds it right now; the next run checks it again
            logger.LogDebug("Stripe subscription {SubscriptionId} is being changed elsewhere; skipped by this renewal conversion run", subscriptionId);
            return false;
        }
        catch (Exception e)
        {
            context.ChangeTracker.Clear();
            logger.LogError(e, "Stripe subscription {SubscriptionId} could not be scheduled to continue every 4 weeks", subscriptionId);
            return false;
        }
    }

    private async Task<bool> ConvertStripeRenewal(string subscriptionId)
    {
        var subscription = await FindProviderSubscription(SubscriptionProviders.Stripe, subscriptionId);
        if (subscription == null)
            return false;
        var snapshot = await StripeApi.GetSubscription(subscriptionId);
        ApplyStripeState(subscription, snapshot);
        await context.SaveChangesAsync();
        var before = subscription.RenewalScheduleId;
        await ScheduleStripeRenewal(subscription, snapshot);
        if (subscription.RenewalScheduleId != before)
            return true;
        return await CorrectStripeRenewalSchedule(subscription, snapshot);
    }

    /// <summary>
    /// Hourly recheck: our attached schedule must plan what is booked. One whose marker differs (e.g. an extension phase left
    /// after its booking was dropped) is updated to the expected phases.
    /// </summary>
    private async Task<bool> CorrectStripeRenewalSchedule(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (!IsOurAttachedSchedule(subscription, snapshot) || subscription.RenewalProduct == null || subscription.RenewalPriceCents == null)
            return false;
        var expected = await RenewalRequest(subscription, snapshot);
        var schedule = await StripeApi.GetSchedule(snapshot.ScheduleId);
        if (schedule == null || AcceptedMarkers(subscription, expected).Contains(schedule.Marker))
            return false;
        logger.LogWarning("Renewal schedule {ScheduleId} of Stripe subscription {SubscriptionId} plans {Marker} instead of {Expected}; correcting it",
            snapshot.ScheduleId, subscription.ExternalId, schedule.Marker, StripeRenewalSchedule.Marker(expected));
        subscription.RenewalScheduleId = await StripeApi.ScheduleRenewal(snapshot, expected);
        subscription.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return true;
    }

    /// <summary>A 13 or 52 week subscription that will renew, billing its own plan, with the schedule we attached</summary>
    private static bool IsOurAttachedSchedule(UserSubscription subscription, StripeSubscriptionSnapshot snapshot) =>
        SubscriptionRenewal.ContinuesAsFourWeeks(subscription.Product)
        && (snapshot.Status is "active" or "past_due")
        && !snapshot.CancelAtPeriodEnd && snapshot.CancelAt == null && !snapshot.HasPendingUpdate
        && snapshot.CurrentPeriodEnd != null && StripePlans.SlugOf(snapshot) == subscription.Product.Slug
        && snapshot.ScheduleId != null && snapshot.ScheduleId == subscription.RenewalScheduleId;

    /// <summary>
    /// The expected marker; during a started extension term also the one the schedule was made with (its extension phase is the
    /// current one then)
    /// </summary>
    private static string[] AcceptedMarkers(UserSubscription subscription, StripeRenewalRequest expected)
    {
        var plain = StripeRenewalSchedule.Marker(expected);
        if (subscription.ExtensionStartsAt is not DateTime start || start > DateTime.UtcNow || subscription.ExtensionPriceCents is not long cents)
            return [plain];
        var plan = subscription.Product;
        var started = new StripeExtensionTerm(cents, plan.Slug, plan.Title, IntervalWeeks(plan.OwnershipSeconds).Value);
        return [plain, StripeRenewalSchedule.Marker(expected with { Extension = started })];
    }

    /// <summary>
    /// PayPal moves a subscription to another plan only with the payer's approval (revise), so a 13 or 52 week PayPal
    /// subscription on a plan created before the 4-week renewal would renew for its full period. Those renewing soon are
    /// logged until they are decided on (payer approval of a 4-week plan, or a cancellation before the renewal).
    /// </summary>
    private async Task WarnAboutLegacyPayPalRenewals(CancellationToken cancellation)
    {
        var renewingBefore = DateTime.UtcNow.AddDays(LegacyPayPalWarningDays);
        var legacy = await context.Subscriptions
            .Where(s => s.ProviderSlug == SubscriptionProviders.PayPal && s.Status == "active" && s.RenewalProduct == null
                && s.Product.OwnershipSeconds > SubscriptionRenewal.MaxFourWeekSeconds && s.RenewsAt < renewingBefore)
            .Select(s => s.ExternalId).ToListAsync(cancellation);
        if (legacy.Count > 0)
            logger.LogWarning("{Count} PayPal subscriptions of 13 or 52 weeks renew for their full period within {Days} days: {SubscriptionIds}",
                legacy.Count, LegacyPayPalWarningDays, string.Join(",", legacy));
    }

    /// <summary>
    /// PayPal: every sale after the first one is a 4-week REGULAR cycle, so a 13 or 52 week subscription converts with it
    /// </summary>
    private async Task ConvertPayPalRenewal(UserSubscription subscription)
    {
        if (InFirstLongTerm(subscription) && await HasGrantedPeriod(subscription))
            ConvertToRenewalPlan(subscription);
    }
}
