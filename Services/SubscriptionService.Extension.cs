using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Owner decision: before a 13 or 52 week term ends the buyer can opt in to one more term of the same length instead of
/// continuing every 4 weeks. After that term the subscription continues as the 4-week plan again, and can be extended again.
/// The term costs the long plan's current list price with the subscription's creator code and "forever" discount code
/// (<see cref="RecurringPriceCents"/>, like the renewal price). Stripe adds an extension phase to the renewal schedule; PayPal
/// needs the payer's approval of a new subscription on the same plan starting when the current term ends, which then
/// replaces the current one (<see cref="LinkPayPalContinuation"/>).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>An extension can be booked until this long before the term ends</summary>
    internal static readonly TimeSpan ExtensionCutoff = TimeSpan.FromHours(24);
    /// <summary>
    /// An extension can be booked from this many days before the term ends (SUBSCRIPTIONS:EXTENSION_BOOKING_WINDOW_DAYS); keeps
    /// PayPal's start time and approval links short-lived. The renewal reminder comes 35 days before.
    /// </summary>
    internal const int DefaultExtensionBookingWindowDays = 60;
    /// <summary>
    /// Plan switches wait until this long after a booked extension started, so a proration can never be mistaken for the
    /// extension's invoice
    /// </summary>
    internal static readonly TimeSpan PlanChangeExtensionGuard = TimeSpan.FromDays(2);

    /// <summary>Error of the extend endpoint while SUBSCRIPTIONS:EXTENSIONS_ENABLED is off</summary>
    internal const string ExtensionUnavailable = "extension_unavailable";

    private SubscriptionDeclarationService ExtensionDeclarations => new(context, null, logger);

    /// <summary>
    /// Books one more term of the same length after the current one (Stripe), or returns the link the payer approves it at (PayPal)
    /// </summary>
    public Task<SubscriptionExtensionResult> ExtendLongTerm(string userId, string subscriptionId, SubscriptionExtensionRequest request) =>
        WithSubscriptionLock(subscriptionId, () => ExtendLongTermLocked(userId, subscriptionId, request));

    private async Task<SubscriptionExtensionResult> ExtendLongTermLocked(string userId, string subscriptionId, SubscriptionExtensionRequest request)
    {
        if (!extensionsEnabled)
            throw new ApiException(ExtensionUnavailable);
        SubscriptionDeclarationService.ValidateExtension(request?.Declaration);
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        if (InTrial(subscription, DateTime.UtcNow))
            throw new ApiException(TrialActive);
        if (await LoadPendingTermSwitch(subscription) != null)
            throw new ApiException(TermSwitchPending);
        if (!await CanExtendLongTerm(subscription))
            throw new ApiException("This subscription can not be extended for another term.");
        var cents = await ExtensionPriceCents(subscription)
            ?? throw new ApiException("The extension can not be priced right now. Please try again later.");
        if (SubscriptionProviders.Normalize(subscription.ProviderSlug) == SubscriptionProviders.Stripe)
            return await ExtendStripe(subscription, cents, request.Declaration);
        return await ExtendPayPal(subscription, cents, request);
    }

    /// <summary>
    /// A direct, active, uncancelled subscription in a 13 or 52 week term that would continue every 4 weeks, not extended for this
    /// term end yet, whose term ends within <paramref name="bookingWindow"/> but more than <see cref="ExtensionCutoff"/> from now
    /// </summary>
    internal static bool MayExtendLongTerm(UserSubscription subscription, DateTime now, TimeSpan bookingWindow) =>
        IsDirectSubscription(subscription) && subscription.Status == "active" && subscription.EndsAt == null
        && InFirstLongTerm(subscription) && subscription.RenewalPriceCents != null
        && !IsExtendedFor(subscription, subscription.RenewsAt)
        && subscription.RenewsAt > now + ExtensionCutoff && subscription.RenewsAt <= now + bookingWindow;

    private static bool IsDirectSubscription(UserSubscription subscription) =>
        SubscriptionProviders.Normalize(subscription.ProviderSlug) is SubscriptionProviders.Stripe or SubscriptionProviders.PayPal;

    private static bool IsPayPal(UserSubscription subscription) =>
        SubscriptionProviders.Normalize(subscription.ProviderSlug) == SubscriptionProviders.PayPal;

    /// <summary>Whether one more term is booked to start at <paramref name="termEnd"/></summary>
    internal static bool IsExtendedFor(UserSubscription subscription, DateTime termEnd) =>
        subscription.ExtensionStartsAt is DateTime start && Math.Abs((start - termEnd).TotalSeconds) < 1;

    /// <summary>An extension booked for the current term that has not started yet (and was not dropped)</summary>
    private static bool HasPendingExtension(UserSubscription subscription, DateTime now) =>
        IsExtendedFor(subscription, subscription.RenewsAt) && subscription.RenewsAt > now;

    /// <summary>
    /// An extension booked for the current term that still stands: Stripe's while the subscription is not cancelled, PayPal's
    /// while its continuation is active (the replaced subscription itself is cancelled by design)
    /// </summary>
    private async Task<bool> HasBookedExtension(UserSubscription subscription, DateTime now)
    {
        if (!HasPendingExtension(subscription, now))
            return false;
        if (!IsPayPal(subscription))
            return subscription.EndsAt == null;
        return subscription.ExtensionSubscriptionId != null && await IsActiveContinuation(subscription.ExtensionSubscriptionId);
    }

    /// <summary>
    /// New bookings only while SUBSCRIPTIONS:EXTENSIONS_ENABLED is on (booked extensions are handled either way); also requires
    /// the current term to be paid (a PayPal continuation is not extendable before its first payment)
    /// </summary>
    private async Task<bool> CanExtendLongTerm(UserSubscription subscription) =>
        extensionsEnabled && MayExtendLongTerm(subscription, DateTime.UtcNow, extensionBookingWindow) && await IsTermPaid(subscription);

    /// <summary>
    /// Whether a period of the subscription was granted. Queried by user first (FiniteTransactions has an index on the user, not
    /// on the subscription id); the subscription list loads subscriptions without their user, so its key is read from the entry.
    /// </summary>
    private Task<bool> IsTermPaid(UserSubscription subscription)
    {
        var userId = UserIdOf(subscription);
        return context.FiniteTransactions.AnyAsync(t => t.User.Id == userId && t.SubscriptionId == subscription.ExternalId);
    }

    private int? UserIdOf(UserSubscription subscription) =>
        subscription.User?.Id ?? context.Entry(subscription).Property<int?>("UserId").CurrentValue;

    /// <summary>What one more term costs, null when it can not be priced (no VAT rate) or is below the minimum charge</summary>
    private async Task<long?> ExtensionPriceCents(UserSubscription subscription)
    {
        var cents = await RecurringPriceCents(subscription, (TopUpProduct)subscription.Product, subscription.CheckoutCountry);
        return cents >= SubscriptionPricing.MinimumCents ? cents : null;
    }

    private static DateTime ExtensionEnd(UserSubscription subscription, DateTime termEnd) =>
        termEnd.AddDays(7 * IntervalWeeks(subscription.Product.OwnershipSeconds).Value);

    private static SubscriptionExtensionResult ExtensionResult(UserSubscription subscription, DateTime termEnd, string approvalUrl) => new()
    {
        Extended = approvalUrl == null, ApprovalUrl = approvalUrl, TermEndsAt = termEnd,
        ExtendedUntil = ExtensionEnd(subscription, termEnd)
    };

    /// <summary>Plan switches wait until a booked extension's invoice was surely handled</summary>
    internal static bool ExtensionBlocksPlanChange(UserSubscription subscription, DateTime now) =>
        subscription.ExtensionStartsAt is DateTime start && now < start + PlanChangeExtensionGuard;

    /// <summary>
    /// Stripe: the renewal schedule gets the extension phase (the long plan at the extension price for one term) before the
    /// 4-week phase. The declaration and the booked price are stored before Stripe is called, so the extension's invoice is
    /// recognised whenever Stripe bills it; a failed Stripe call drops the booking again.
    /// </summary>
    private async Task<SubscriptionExtensionResult> ExtendStripe(UserSubscription subscription, long cents, SubscriptionDeclaration declaration)
    {
        var snapshot = await StripeApi.GetSubscription(subscription.ExternalId);
        await ApplyCheckedStripeState(subscription, snapshot);
        if (!MayExtendLongTerm(subscription, DateTime.UtcNow, extensionBookingWindow) || snapshot.HasPendingUpdate
            || snapshot.CurrentPeriodEnd == null || StripePlans.SlugOf(snapshot) != subscription.Product.Slug)
            throw new ApiException("This subscription can not be extended for another term.");
        var termEnd = snapshot.CurrentPeriodEnd.Value;
        await ExtensionDeclarations.RecordExtension(declaration, subscription.User, (TopUpProduct)subscription.Product,
            SubscriptionDeclarationService.ExtensionReference(subscription.ExternalId, termEnd), null, termEnd);
        (subscription.ExtensionStartsAt, subscription.ExtensionPriceCents) = (termEnd, cents);
        subscription.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        await ScheduleStripeExtension(subscription, snapshot);
        logger.LogInformation("Stripe subscription {SubscriptionId} is extended by another {Plan} term at {Cents} cents from {TermEnd} (schedule {ScheduleId})",
            subscription.ExternalId, subscription.Product.Slug, cents, termEnd, subscription.RenewalScheduleId);
        return ExtensionResult(subscription, termEnd, null);
    }

    private async Task ScheduleStripeExtension(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        StripeRenewalRequest request = null;
        try
        {
            request = await RenewalRequest(subscription, snapshot);
            subscription.RenewalScheduleId = await StripeApi.ScheduleRenewal(snapshot, request);
            await context.SaveChangesAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Scheduling the extension of Stripe subscription {SubscriptionId} failed; checking whether Stripe applied it",
                subscription.ExternalId);
            await KeepOrDropStripeExtension(subscription, snapshot, request);
        }
    }

    /// <summary>
    /// The schedule update may have reached Stripe although its response (or our save) failed: a schedule carrying this
    /// extension's marker keeps the booking, otherwise it is dropped and the buyer asked to try again
    /// </summary>
    private async Task KeepOrDropStripeExtension(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeRenewalRequest request)
    {
        if (request != null && await TryKeepAppliedExtension(subscription, snapshot, request))
            return;
        (subscription.ExtensionStartsAt, subscription.ExtensionPriceCents) = (null, null);
        await context.SaveChangesAsync();
        throw new ApiException("The extension could not be booked right now. Please try again later.");
    }

    private async Task<bool> TryKeepAppliedExtension(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeRenewalRequest request)
    {
        try
        {
            var scheduleId = await AppliedExtensionSchedule(subscription, request);
            if (scheduleId == null)
                return false;
            subscription.RenewalScheduleId = scheduleId;
            await context.SaveChangesAsync();
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(e, "The schedule of Stripe subscription {SubscriptionId} could not be read; scheduling it without the extension",
                subscription.ExternalId);
            await TryScheduleWithoutExtension(subscription, snapshot, request);
            return false;
        }
    }

    /// <summary>The attached schedule when it carries exactly this extension, else null; throws when Stripe can not be read</summary>
    private async Task<string> AppliedExtensionSchedule(UserSubscription subscription, StripeRenewalRequest request)
    {
        var scheduleId = (await StripeApi.GetSubscription(subscription.ExternalId)).ScheduleId;
        if (scheduleId == null)
            return null;
        var schedule = await StripeApi.GetSchedule(scheduleId);
        return schedule?.Marker == StripeRenewalSchedule.Marker(request) ? scheduleId : null;
    }

    /// <summary>
    /// Whether the extension reached Stripe is unknown: the schedule is set to the plain renewal, so Stripe never bills a term the
    /// dropped booking does not cover. A failure is left to the hourly recheck (<see cref="CorrectStripeRenewalSchedule"/>).
    /// </summary>
    private async Task TryScheduleWithoutExtension(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeRenewalRequest request)
    {
        try
        {
            subscription.RenewalScheduleId = await StripeApi.ScheduleRenewal(snapshot, request with { Extension = null });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Stripe subscription {SubscriptionId} could not be scheduled without the dropped extension; the hourly recheck corrects it",
                subscription.ExternalId);
        }
    }

    /// <summary>The extension term a renewal schedule contains: the one booked for the end of the current period</summary>
    private static StripeExtensionTerm ExtensionTerm(UserSubscription subscription, StripeSubscriptionSnapshot snapshot)
    {
        if (subscription.ExtensionPriceCents is not long cents || snapshot.CurrentPeriodEnd is not DateTime periodEnd
            || !IsExtendedFor(subscription, periodEnd))
            return null;
        var plan = subscription.Product;
        return new StripeExtensionTerm(cents, plan.Slug, plan.Title, IntervalWeeks(plan.OwnershipSeconds).Value);
    }

    /// <summary>
    /// The first invoice of the extension term: around the term end the extension was booked for, Stripe bills the long plan at
    /// the extension price. A proration (it has a plan change, or a period was granted since the term end, e.g. the extension
    /// itself) is never it. It pays the extension price and does not convert the subscription to its 4-week plan.
    /// </summary>
    internal async Task<bool> IsExtensionInvoice(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice)
    {
        var granted = await IsGranted(subscription.User.ExternalId, SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoice.Id));
        if (!(granted ? WasGrantedAsExtension(subscription, invoice) : BillsExtension(subscription, snapshot, invoice)))
            return false;
        if (await context.SubscriptionPlanChanges.AnyAsync(c => c.InvoiceId == invoice.Id))
            return false;
        return granted || !await HasOtherGrantSince(subscription, subscription.ExtensionStartsAt.Value.AddDays(-1), invoice.Id);
    }

    /// <summary>
    /// A redelivery of a granted invoice (Stripe retries for up to 3 days) is the extension's when it was created around the
    /// booked term end; what Stripe bills by then (a later price) does not matter
    /// </summary>
    private static bool WasGrantedAsExtension(UserSubscription subscription, StripeInvoiceInfo invoice) =>
        subscription.ExtensionStartsAt is DateTime start && subscription.ExtensionPriceCents != null
        && invoice.NormalizedBillingReason is "renewal" or "updated"
        && invoice.Created > start.AddDays(-1) && invoice.Created < start + GrantedExtensionInvoiceWindow;

    /// <summary>How long after the term end a granted invoice can still be the extension's (2 days plus Stripe's 3 retry days)</summary>
    private static readonly TimeSpan GrantedExtensionInvoiceWindow = TimeSpan.FromDays(5);

    private static bool BillsExtension(UserSubscription subscription, StripeSubscriptionSnapshot snapshot, StripeInvoiceInfo invoice) =>
        subscription.ExtensionStartsAt is DateTime start && subscription.ExtensionPriceCents is long cents && InFirstLongTerm(subscription)
        && invoice.NormalizedBillingReason is "renewal" or "updated"
        && StripePlans.SlugOf(snapshot) == subscription.Product.Slug && snapshot.UnitAmount == cents
        && invoice.Created > start.AddDays(-1) && invoice.Created < start.AddDays(2);

    /// <summary>A period of the subscription granted since <paramref name="since"/> by another invoice than this one</summary>
    private Task<bool> HasOtherGrantSince(UserSubscription subscription, DateTime since, string invoiceId)
    {
        var own = SubscriptionProviders.GrantReference(SubscriptionProviders.Stripe, invoiceId);
        var userId = subscription.User.Id;
        return context.FiniteTransactions.AnyAsync(t => t.User.Id == userId && t.SubscriptionId == subscription.ExternalId
            && t.Amount < 0 && t.Timestamp >= since && t.Reference != own);
    }

    /// <summary>
    /// Cancelling a subscription cancels the extension booked for its current term as well: Stripe's is part of the renewal
    /// schedule the cancellation released; PayPal's continuation is cancelled at PayPal (one still awaiting approval is refused
    /// on activation, see <see cref="AcceptPayPalContinuation"/>). Cancelling a continuation before its term started forgets the
    /// extension of the subscription it continues.
    /// </summary>
    private async Task CancelBookedExtension(UserSubscription subscription)
    {
        var now = DateTime.UtcNow;
        var pending = HasPendingExtension(subscription, now);
        if (subscription.ExtensionSubscriptionId != null && subscription.RenewsAt > now)
            await CancelPayPalContinuation(subscription.ExtensionSubscriptionId);
        if (pending)
            (subscription.ExtensionStartsAt, subscription.ExtensionPriceCents) = (null, null);
        await ForgetExtensionOfPrevious(subscription);
    }

    private async Task ForgetExtensionOfPrevious(UserSubscription subscription)
    {
        if (subscription.PreviousSubscriptionId == null || await IsTermPaid(subscription))
            return;
        var previous = await FindProviderSubscription(SubscriptionProviders.PayPal, subscription.PreviousSubscriptionId);
        if (previous?.ExtensionSubscriptionId == subscription.ExternalId && HasPendingExtension(previous, DateTime.UtcNow))
            (previous.ExtensionStartsAt, previous.ExtensionPriceCents) = (null, null);
    }

    /// <summary>
    /// The current term, its renewal price, and whether and at what price it can be extended (subscription list of the website)
    /// </summary>
    private async Task DescribeTerms(UserSubscription subscription)
    {
        subscription.CurrentTermEndsAt = subscription.EndsAt ?? subscription.RenewsAt;
        if (!IsDirectSubscription(subscription))
            return;
        var renewalCents = InFirstLongTerm(subscription) ? subscription.RenewalPriceCents : subscription.PriceCents;
        subscription.RenewalPrice = MajorUnits(renewalCents, subscription.Currency);
        subscription.ExtensionTaxTreatment = PriceTaxTreatment(subscription);
        var booked = await HasBookedExtension(subscription, DateTime.UtcNow);
        subscription.ExtendedUntil = booked ? ExtensionEnd(subscription, subscription.RenewsAt) : null;
        try
        {
            await DescribeExtensionOffer(subscription, booked);
        }
        catch (Exception e)
        {
            // e.g. a discount code that can not be read: the list still loads, just without the offer
            logger.LogError(e, "The extension offer of subscription {SubscriptionId} could not be priced", subscription.ExternalId);
            (subscription.CanExtendLongTerm, subscription.ExtensionPrice) = (false, booked ? subscription.ExtensionPrice : 0);
        }
    }

    private async Task DescribeExtensionOffer(UserSubscription subscription, bool booked)
    {
        if (booked)
        {
            subscription.ExtensionPrice = MajorUnits(subscription.ExtensionPriceCents, subscription.Currency);
            return;
        }
        var extensionCents = await CanExtendLongTerm(subscription) ? await ExtensionPriceCents(subscription) : null;
        subscription.CanExtendLongTerm = extensionCents != null;
        subscription.ExtensionPrice = MajorUnits(extensionCents, subscription.Currency);
    }

    /// <summary>How tax relates to the renewal and extension prices (as <see cref="SubscriptionEventTerms"/> states it)</summary>
    private string PriceTaxTreatment(UserSubscription subscription) =>
        SubscriptionEventTerms.PriceTaxTreatment(subscription.ManagedPayments, null, tax?.RateFor(subscription.CheckoutCountry) ?? 0);

    private static decimal MajorUnits(long? cents, string currency) =>
        cents is long value ? StripeAmounts.FromMinorUnits(value, currency) : 0;
}
