using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stripe;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The subscription schedule that moves a 13 or 52 week Stripe subscription to its 4-week renewal price at the end of
/// the paid term. Two phases: the current one, kept as Stripe derived it from the subscription but ending with the term,
/// and the renewal phase (4-week price, new billing cycle from its start, no prorations). When the buyer opted in to one more
/// term of the same length, an extension phase (the long plan at the extension price, one term) comes between them. The schedule then releases the
/// subscription (end_behavior release), which keeps billing the 4-week price until cancelled.
/// Managed Payments subscriptions support schedules since February 2026 (Managed Payments changelog).
/// </summary>
public static class StripeRenewalSchedule
{
    /// <summary>
    /// Attaches the renewal schedule: from_subscription (or the schedule already attached), then the update with both phases.
    /// A schedule created here is released again when completing it fails, so no half made schedule (only the current
    /// phase) stays attached; Stripe would refuse cancel_at_period_end on it. Returns the schedule id.
    /// </summary>
    public static async Task<string> Schedule(StripeSubscriptionSnapshot current, StripeRenewalRequest request,
        IStripeScheduleCalls stripe, DateTime now)
    {
        var created = current.ScheduleId == null;
        var schedule = created ? await stripe.CreateFrom(current.Id) : await stripe.Get(current.ScheduleId);
        try
        {
            return await Complete(schedule, current, request, stripe, now);
        }
        catch (Exception) when (created)
        {
            await ReleaseQuietly(stripe, schedule.Id);
            throw;
        }
    }

    private static async Task<string> Complete(SubscriptionSchedule schedule, StripeSubscriptionSnapshot current,
        StripeRenewalRequest request, IStripeScheduleCalls stripe, DateTime now)
    {
        if (IsDoneFor(schedule, request))
            return schedule.Id;
        if (HasForeignPhases(schedule, now))
            throw new ApiException($"Stripe subscription {current.Id} has schedule {schedule.Id} with other planned changes");
        var phase = CurrentPhase(schedule, now)
            ?? throw new ApiException($"Stripe schedule {schedule.Id} of {current.Id} has no current phase");
        var product = await stripe.EnsurePlanProduct(request.PlanSlug, request.PlanName);
        var extensionProduct = request.Extension == null ? null
            : await stripe.EnsurePlanProduct(request.Extension.PlanSlug, request.Extension.PlanName);
        return (await stripe.Update(schedule.Id, Update(phase, request, product, extensionProduct))).Id;
    }

    /// <summary>A failed release leaves the one phase schedule; cancelling releases it (<c>ReleaseStripeRenewal</c>)</summary>
    private static async Task ReleaseQuietly(IStripeScheduleCalls stripe, string scheduleId)
    {
        try
        {
            await stripe.Release(scheduleId);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Schedule metadata naming the renewal (plan and cents) a schedule was made for</summary>
    public const string MarkerKey = "coflnet_renewal";

    public static string Marker(StripeRenewalRequest request) => request.Extension == null
        ? $"{request.PlanSlug}:{request.UnitAmount}"
        : $"{request.PlanSlug}:{request.UnitAmount}+{request.Extension.PlanSlug}:{request.Extension.UnitAmount}";

    /// <summary>True when the schedule already moves the subscription to exactly this renewal</summary>
    public static bool IsDoneFor(SubscriptionSchedule schedule, StripeRenewalRequest request) =>
        schedule?.Metadata?.GetValueOrDefault(MarkerKey) == Marker(request) && schedule.Phases?.Count >= 2
        && schedule.Status is "active" or "not_started";

    /// <summary>A schedule we made for a renewal (any plan and price)</summary>
    public static bool IsRenewalSchedule(SubscriptionSchedule schedule) => schedule?.Metadata?.ContainsKey(MarkerKey) == true;

    /// <summary>
    /// A schedule someone else made with changes still to come (e.g. in the dashboard): it is not overwritten
    /// </summary>
    public static bool HasForeignPhases(SubscriptionSchedule schedule, DateTime now) =>
        schedule.Metadata?.ContainsKey(MarkerKey) != true && (schedule.Phases?.Count(p => p.EndDate > now) ?? 0) > 1;

    /// <summary>The phase running now (the last started one)</summary>
    public static SubscriptionSchedulePhase CurrentPhase(SubscriptionSchedule schedule, DateTime now) =>
        schedule.Phases?.Where(p => p.StartDate <= now).OrderBy(p => p.StartDate).LastOrDefault();

    /// <summary>
    /// The update that replaces the schedule's phases with the current one, the extension term the buyer opted in to (if any)
    /// and the renewal phase. <paramref name="extensionProductId"/> is the plan product of the extension (required with one).
    /// </summary>
    public static SubscriptionScheduleUpdateOptions Update(SubscriptionSchedulePhase current, StripeRenewalRequest request, string productId,
        string extensionProductId = null) => new()
    {
        Phases = request.Extension == null
            ? [KeepCurrent(current, request.TermEnd), Renewal(request, productId)]
            : [KeepCurrent(current, request.TermEnd), Extension(request, extensionProductId), Renewal(request, productId)],
        EndBehavior = "release",
        ProrationBehavior = "none",
        Metadata = new Dictionary<string, string> { [MarkerKey] = Marker(request) }
    };

    // Lists are only set when they have entries: Stripe.net sends a list that was set, even to null, as an empty value,
    // which would unset it (and Managed Payments subscriptions have no tax rates to send).
    private static SubscriptionSchedulePhaseOptions KeepCurrent(SubscriptionSchedulePhase current, DateTime termEnd)
    {
        var phase = new SubscriptionSchedulePhaseOptions
        {
            StartDate = current.StartDate,
            EndDate = termEnd,
            Items = current.Items.Select(KeepItem).ToList(),
            ProrationBehavior = "none"
        };
        if (current.Discounts?.Count > 0)
            phase.Discounts = current.Discounts.Select(KeepDiscount).ToList();
        return phase;
    }

    private static SubscriptionSchedulePhaseItemOptions KeepItem(SubscriptionSchedulePhaseItem item)
    {
        var kept = new SubscriptionSchedulePhaseItemOptions { Price = item.PriceId, Quantity = item.Quantity };
        if (item.TaxRates?.Count > 0)
            kept.TaxRates = item.TaxRates.Select(t => t.Id).ToList();
        return kept;
    }

    /// <summary>Stripe takes one of coupon, discount and promotion_code; the existing discount is kept as it is</summary>
    private static SubscriptionSchedulePhaseDiscountOptions KeepDiscount(SubscriptionSchedulePhaseDiscount discount)
    {
        if (discount.DiscountId != null)
            return new() { Discount = discount.DiscountId };
        if (discount.PromotionCodeId != null)
            return new() { PromotionCode = discount.PromotionCodeId };
        return new() { Coupon = discount.CouponId };
    }

    private static SubscriptionSchedulePhaseItemOptions PhaseItem(StripeRenewalRequest request, string productId, long unitAmount, int weeks)
    {
        var item = new SubscriptionSchedulePhaseItemOptions
        {
            Quantity = 1,
            PriceData = new SubscriptionSchedulePhaseItemPriceDataOptions
            {
                Product = productId,
                Currency = request.Currency,
                UnitAmount = unitAmount,
                TaxBehavior = request.TaxBehavior,
                Recurring = new SubscriptionSchedulePhaseItemPriceDataRecurringOptions { Interval = "week", IntervalCount = weeks }
            }
        };
        if (request.TaxRateId != null)
            item.TaxRates = [request.TaxRateId];
        return item;
    }

    private static SubscriptionSchedulePhaseOptions Renewal(StripeRenewalRequest request, string productId)
    {
        var phase = BilledPhase(PhaseItem(request, productId, request.UnitAmount, SubscriptionRenewal.RenewalWeeks), request.UnitAmount);
        phase.Duration = new SubscriptionSchedulePhaseDurationOptions { Interval = "week", IntervalCount = SubscriptionRenewal.RenewalWeeks };
        return phase;
    }

    /// <summary>
    /// One term of the plan's own length at the extension price, ending at an explicit date (the term end plus the plan's weeks,
    /// what the subscription stores). Creator and "forever" discount codes are part of the unit amount (as at checkout), so no
    /// coupon applies; a "once" code never does.
    /// </summary>
    private static SubscriptionSchedulePhaseOptions Extension(StripeRenewalRequest request, string productId)
    {
        var extension = request.Extension;
        var phase = BilledPhase(PhaseItem(request, productId, extension.UnitAmount, extension.Weeks), extension.UnitAmount);
        phase.EndDate = ExtensionEnd(request);
        return phase;
    }

    /// <summary>Where the extension phase ends and the 4-week phase starts</summary>
    public static DateTime ExtensionEnd(StripeRenewalRequest request) => request.TermEnd.AddDays(7 * request.Extension.Weeks);

    private static SubscriptionSchedulePhaseOptions BilledPhase(SubscriptionSchedulePhaseItemOptions item, long unitAmount) => new()
    {
        Items = [item],
        // The cycle starts when the previous term ends; nothing is prorated or charged before
        BillingCycleAnchor = "phase_start",
        ProrationBehavior = "none",
        // The subscription metadata then names the price every later invoice must charge
        Metadata = new Dictionary<string, string>
        {
            [SubscriptionService.StripeMetadata.PriceCents] = unitAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }
    };
}

/// <summary>
/// The Stripe calls <see cref="StripeRenewalSchedule.Schedule"/> makes (faked in tests)
/// </summary>
public interface IStripeScheduleCalls
{
    Task<SubscriptionSchedule> CreateFrom(string subscriptionId);
    Task<SubscriptionSchedule> Get(string scheduleId);
    Task<SubscriptionSchedule> Update(string scheduleId, SubscriptionScheduleUpdateOptions options);
    Task Release(string scheduleId);
    /// <summary>The Stripe product of a plan (coflnet_plan_{slug}), created when missing</summary>
    Task<string> EnsurePlanProduct(string planSlug, string planName);
}
