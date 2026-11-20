using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coflnet.Payments.Services;

/// <summary>
/// What the payment event of a term switch states besides the purchase of the new term
/// </summary>
/// <param name="PreviousWeeks">Length of the term before the switch (4 or 13)</param>
/// <param name="PreviousAccessEndsAt">When the access paid before the switch ended; the new term is added from there</param>
/// <param name="AccessEndsAt">The access end after the switch</param>
/// <param name="NextBillingAt">When the first 4-week cycle after the new term is charged</param>
/// <param name="PromotionCode">The automatic promotion that lowered the payment, null without one</param>
public record SubscriptionTermSwitchInfo(int PreviousWeeks, DateTime PreviousAccessEndsAt, DateTime AccessEndsAt, DateTime NextBillingAt,
    string PromotionCode);

/// <summary>
/// Owner decision: a running direct (Stripe or PayPal) subscription can switch to a longer term of the same tier at once: 4 → 13 or
/// 52 weeks, 13 → 52 weeks. The new term is charged now at its price (the subscription's creator code and lasting discount code, or
/// the active automatic promotion when that is lower, first charge only); the paid days left of the current term are not refunded
/// but kept: the new term is granted on top of the current access end. After the new term the subscription continues every 4 weeks
/// like any 13/52 week term (<see cref="SubscriptionRenewal"/>). Stripe changes the subscription in place (billing restarts now, no
/// proration credit, applied only once paid); PayPal can not change a cycle's length, so a new PayPal subscription of the new term
/// replaces the current one once the payer approved it. Switched on with SUBSCRIPTIONS:TERM_SWITCH_ENABLED (default true).
/// </summary>
public partial class SubscriptionService
{
    /// <summary>Error of the switch endpoint while SUBSCRIPTIONS:TERM_SWITCH_ENABLED is false (and the options' reason)</summary>
    internal const string TermSwitchUnavailable = "term_switch_unavailable";
    /// <summary>The switch would charge another amount than the buyer was shown (e.g. the promotion ended or was used up)</summary>
    internal const string TermSwitchPriceChanged = "term_switch_price_changed";
    /// <summary>Another switch waits for the buyer's payment or approval</summary>
    internal const string TermSwitchPending = "term_switch_pending";
    /// <summary>
    /// How long a pending switch blocks another one: Stripe drops a pending update that was not paid within 23 hours
    /// (docs.stripe.com/billing/subscriptions/pending-updates)
    /// </summary>
    internal static readonly TimeSpan TermSwitchPendingFor = TimeSpan.FromHours(23);
    /// <summary>
    /// How long a switch whose outcome Stripe could not tell (the call failed and the subscription could not be read) blocks another
    /// attempt; its invoice is still recognised for <see cref="TermSwitchPendingFor"/>
    /// </summary>
    internal static readonly TimeSpan UnknownTermSwitchBackoff = TimeSpan.FromMinutes(15);
    /// <summary>The switch request must name the price the buyer was shown</summary>
    internal const string ExpectedPriceRequired = "expected_charged_now_required";

    /// <summary>The price of one longer term for a subscription, as offered now</summary>
    private sealed record TermSwitchQuote(TopUpProduct Plan, int Weeks, long RegularCents, long ChargedCents, ValidatedDiscount Promotion,
        TopUpProduct RenewalPlan, long RenewalCents, DateTime AccessEndsAt, DateTime Now)
    {
        /// <summary>The grant adds the plan's ownership time (52 weeks: 365 days) to the access end</summary>
        public DateTime NewAccessEndsAt => AccessEndsAt.AddSeconds(Plan.OwnershipSeconds);
        public DateTime FirstRenewalAt => Now.AddDays(7 * Weeks);
        public string PromotionCode => Promotion?.Code;
    }

    private DiscountCodeService DiscountCodes => new(context, NullLogger<DiscountCodeService>.Instance);

    /// <summary>The longer terms the subscription can switch to now, priced, with what happens to the paid days left</summary>
    public async Task<SubscriptionTermOptions> GetTermOptions(string userId, string subscriptionId)
    {
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        var options = new SubscriptionTermOptions
        {
            SubscriptionId = subscriptionId, CurrentWeeks = IntervalWeeks(subscription.Product.OwnershipSeconds),
            UnavailableReason = await TermSwitchBlocker(subscription, DateTime.UtcNow)
        };
        if (options.UnavailableReason != null)
            return options;
        options.CurrentAccessEndsAt = await AccessEnd(subscription, DateTime.UtcNow);
        options.Pending = await PendingTermSwitchResult(subscription);
        options.Terms = (await TermSwitchQuotes(subscription)).Select(q => TermOption(subscription, q)).ToList();
        if (options.Terms.Count == 0)
            options.UnavailableReason = "no_longer_term";
        return options;
    }

    /// <summary>
    /// Switches to the longer term of <see cref="SubscriptionTermSwitchRequest.Weeks"/>: Stripe charges it at once (or returns the page
    /// where the buyer confirms the payment), PayPal returns the link where the payer approves the new subscription
    /// </summary>
    public Task<SubscriptionTermSwitchResult> SwitchTerm(string userId, string subscriptionId, SubscriptionTermSwitchRequest request) =>
        WithSubscriptionLock(subscriptionId, () => SwitchTermLocked(userId, subscriptionId, request));

    private async Task<SubscriptionTermSwitchResult> SwitchTermLocked(string userId, string subscriptionId, SubscriptionTermSwitchRequest request)
    {
        if (!termSwitchEnabled)
            throw new ApiException(TermSwitchUnavailable);
        SubscriptionDeclarationService.ValidateExtension(request?.Declaration);
        if (request.ExpectedChargedNow is not decimal expected)
            throw new ApiException(ExpectedPriceRequired);
        var subscription = await OwnedSubscription(userId, subscriptionId)
            ?? throw new ApiException("Subscription not found");
        if (await TermSwitchBlocker(subscription, DateTime.UtcNow) is string blocker)
            throw new ApiException(blocker);
        var pending = await PendingTermSwitch(subscription, request.Weeks);
        if (pending != null)
            return TermSwitchResult(subscription, pending, PendingStatus(pending), pending.CreatedAt.AddDays(7 * pending.Weeks));
        var quote = (await TermSwitchQuotes(subscription)).FirstOrDefault(q => q.Weeks == request.Weeks)
            ?? throw new ApiException("term_switch_not_offered");
        // compared in cents: clients may pass the price through a double (19.77 → 19.769999999999999)
        if (StripeAmounts.ToMinorUnits(expected, subscription.Currency) != quote.ChargedCents)
            throw new ApiException(TermSwitchPriceChanged);
        if (IsPayPal(subscription))
            return await SwitchPayPalTerm(subscription, quote, request);
        return await SwitchStripeTerm(subscription, quote, request);
    }

    /// <summary>Why the subscription can not switch its term now, null when it can</summary>
    private async Task<string> TermSwitchBlocker(UserSubscription subscription, DateTime now)
    {
        if (!termSwitchEnabled || !IsDirectSubscription(subscription) || (IsPayPal(subscription) ? paypal == null : stripe == null))
            return TermSwitchUnavailable;
        if (InTrial(subscription, now))
            return TrialActive;
        if (subscription.Status != "active" || subscription.EndsAt != null)
            return "subscription_not_active";
        if (ExtensionBlocksPlanChange(subscription, now) || await HasLivePayPalExtension(subscription, now))
            return "extension_booked";
        // Releasing a 13 week term's renewal schedule this close to its renewal could let Stripe renew it for 13 weeks again
        if (subscription.RenewsAt < now + ExtensionCutoff)
            return "renewal_due";
        if (FourWeekPlanOf(subscription) == null)
            return "no_longer_term";
        return await IsTermPaid(subscription) ? null : "term_not_paid";
    }

    /// <summary>
    /// The 4-week plan a subscription bills or (in a 13 week first term) continues as; longer terms of it continue as this plan.
    /// Null when there is none (a 52 week term, a legacy long PayPal plan that repeats its term).
    /// </summary>
    private static Product FourWeekPlanOf(UserSubscription subscription)
    {
        if (InFirstLongTerm(subscription))
            return subscription.RenewalProduct;
        return IntervalWeeks(subscription.Product.OwnershipSeconds) == SubscriptionRenewal.RenewalWeeks ? subscription.Product : null;
    }

    private async Task<List<TermSwitchQuote>> TermSwitchQuotes(UserSubscription subscription)
    {
        var now = DateTime.UtcNow;
        var accessEnd = await AccessEnd(subscription, now);
        var promotion = await ActivePromotion();
        var quotes = new List<TermSwitchQuote>();
        foreach (var plan in await TermSwitchTargets(subscription))
        {
            if (await Quote(subscription, plan, promotion, accessEnd, now) is TermSwitchQuote quote)
                quotes.Add(quote);
        }
        return quotes;
    }

    /// <summary>
    /// The sellable 13/52 week plans longer than the current term that continue as the subscription's 4-week plan (same product line,
    /// tier, slot count and currency, <see cref="SubscriptionRenewalPlans.Find"/>), one per length
    /// </summary>
    private async Task<List<TopUpProduct>> TermSwitchTargets(UserSubscription subscription)
    {
        var basePlan = FourWeekPlanOf(subscription);
        var currentWeeks = IntervalWeeks(subscription.Product.OwnershipSeconds) ?? int.MaxValue;
        var candidates = await context.TopUpProducts.Include(p => p.Groups)
            .Where(p => p.Type.HasFlag(Product.ProductType.SERVICE) && p.SlotCount == subscription.Product.SlotCount).ToListAsync();
        var targets = new List<TopUpProduct>();
        foreach (var plan in candidates.Where(p => IsTermSwitchCandidate(basePlan, p, currentWeeks)))
        {
            if ((await renewalPlans.Find(plan))?.Id == basePlan.Id)
                targets.Add(plan);
        }
        return targets.GroupBy(p => IntervalWeeks(p.OwnershipSeconds))
            .Select(g => g.OrderByDescending(p => p.Slug.StartsWith(basePlan.Slug, StringComparison.Ordinal)).ThenBy(p => p.Id).First())
            .OrderBy(p => p.OwnershipSeconds).ToList();
    }

    private bool IsTermSwitchCandidate(Product basePlan, TopUpProduct plan, int currentWeeks) =>
        IsSubscriptionPlan(plan) && IntervalWeeks(plan.OwnershipSeconds) > currentWeeks && planLines.SameLine(basePlan, plan);

    /// <summary>
    /// Regular price: the plan's list price with the subscription's creator code and lasting discount code (like an extension);
    /// the active automatic promotion (with the creator code, instead of the subscription's code) is charged when lower. The
    /// 4-week continuation is priced like the renewal of any 13/52 week term. Null when it can not be priced.
    /// </summary>
    private async Task<TermSwitchQuote> Quote(UserSubscription subscription, TopUpProduct plan, ValidatedDiscount promotion,
        DateTime accessEnd, DateTime now)
    {
        var weeks = IntervalWeeks(plan.OwnershipSeconds).Value;
        var regular = await RecurringPriceCents(subscription, plan, subscription.CheckoutCountry);
        var renewalPlan = await renewalPlans.Find(plan);
        var renewal = renewalPlan == null ? null : await RecurringPriceCents(subscription, renewalPlan, subscription.CheckoutCountry);
        if (regular == null || renewal == null)
            return null;
        var termPromotion = promotion?.ForPeriod(weeks);
        var promoted = await PromotionCents(subscription, plan, termPromotion);
        var lower = promoted < regular;
        var charged = lower ? promoted.Value : regular.Value;
        if (charged < SubscriptionPricing.MinimumCents)
            return null;
        return new TermSwitchQuote(plan, weeks, regular.Value, charged, lower ? termPromotion : null, renewalPlan, renewal.Value, accessEnd, now);
    }

    private async Task<long?> PromotionCents(UserSubscription subscription, TopUpProduct plan, ValidatedDiscount promotion)
    {
        if (promotion == null)
            return null;
        var creator = string.IsNullOrWhiteSpace(subscription.CreatorCode) ? null
            : await context.CreatorCodes.FirstOrDefaultAsync(c => c.Code.ToLower() == subscription.CreatorCode.ToLower());
        return ChargedCents(SubscriptionPricing.Apply(plan.Price, creator, promotion), subscription.ManagedPayments, subscription.CheckoutCountry);
    }

    /// <summary>The automatic promotion new subscriptions get now (GET /TopUp/discount/auto), null without one</summary>
    private async Task<ValidatedDiscount> ActivePromotion()
    {
        var code = await DiscountCodes.GetActiveAutoApply();
        return code == null ? null : await DiscountTerms(code.Code);
    }

    /// <summary>The access end paid so far (at least now)</summary>
    private async Task<DateTime> AccessEnd(UserSubscription subscription, DateTime now)
    {
        var end = await AccessExpiry(subscription) ?? subscription.RenewsAt;
        return end > now ? end : now;
    }

    private SubscriptionTermOption TermOption(UserSubscription subscription, TermSwitchQuote quote) => new()
    {
        Weeks = quote.Weeks, ProductSlug = quote.Plan.Slug, Title = quote.Plan.Title,
        RegularPrice = MajorUnits(quote.RegularCents, subscription.Currency), ChargedNow = MajorUnits(quote.ChargedCents, subscription.Currency),
        Currency = subscription.Currency, TaxTreatment = PriceTaxTreatment(subscription), Promotion = PromotionOf(quote),
        CurrentAccessEndsAt = quote.AccessEndsAt, NewAccessEndsAt = quote.NewAccessEndsAt,
        CarriedOverDays = (int)Math.Floor((quote.AccessEndsAt - quote.Now).TotalDays), FirstRenewalAt = quote.FirstRenewalAt,
        RenewsAsProductSlug = quote.RenewalPlan.Slug, RenewalPrice = MajorUnits(quote.RenewalCents, subscription.Currency),
        RenewalWeeks = SubscriptionRenewal.RenewalWeeks
    };

    private static SubscriptionTermPromotion PromotionOf(TermSwitchQuote quote) => quote.Promotion == null ? null : new()
    {
        Code = quote.Promotion.Code, Name = quote.Promotion.Name, AmountType = quote.Promotion.AmountType,
        Amount = quote.Promotion.Amount, ExpiresAt = quote.Promotion.ExpiresAt
    };

    /// <summary>
    /// Stores the buyer's declaration for the new term first (a refused one, e.g. a reused id, changes nothing), then counts one use of
    /// the promotion and records the switch (before the provider is called, so its payment is recognised whenever it arrives)
    /// </summary>
    private async Task<SubscriptionTermSwitch> StartTermSwitch(UserSubscription subscription, TermSwitchQuote quote,
        SubscriptionDeclaration declaration)
    {
        var row = NewTermSwitch(subscription, quote);
        await ExtensionDeclarations.RecordExtension(declaration, subscription.User, quote.Plan, TermSwitchReference(row), null, quote.Now);
        if (quote.PromotionCode != null && !await DiscountCodes.Redeem(quote.PromotionCode))
            throw new ApiException(TermSwitchPriceChanged);
        context.SubscriptionTermSwitches.Add(row);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (Exception)
        {
            context.Entry(row).State = EntityState.Detached;
            if (row.PromotionCode != null)
                await DiscountCodes.Release(row.PromotionCode);
            throw;
        }
        return row;
    }

    private static SubscriptionTermSwitch NewTermSwitch(UserSubscription subscription, TermSwitchQuote quote) => new()
    {
        SubscriptionId = subscription.ExternalId, Provider = SubscriptionProviders.Normalize(subscription.ProviderSlug),
        UserId = subscription.User.ExternalId, PreviousProduct = subscription.Product, Product = quote.Plan, RenewalProduct = quote.RenewalPlan,
        Weeks = quote.Weeks, RegularCents = quote.RegularCents, ChargedCents = quote.ChargedCents, RenewalCents = quote.RenewalCents,
        Currency = subscription.Currency, PromotionCode = quote.PromotionCode, AccessEndsBefore = quote.AccessEndsAt,
        Status = TermSwitchStatus.Pending, CreatedAt = quote.Now,
        ExpiresAt = quote.Now + TermSwitchPendingFor
    };

    /// <summary>
    /// The purchase reference of the declaration given for a switch: stable for retries of the same switch (same subscription, term
    /// and access end), so a buyer can retry a failed attempt with the same declaration
    /// </summary>
    internal static string TermSwitchReference(SubscriptionTermSwitch row) =>
        $"subscription-term-switch-{row.SubscriptionId}-{row.Weeks}-{row.AccessEndsBefore.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)}";

    private async Task<ServicePerformanceDeclaration> TermSwitchDeclaration(SubscriptionTermSwitch row)
    {
        var reference = TermSwitchReference(row);
        return await context.ServicePerformanceDeclarations.AsNoTracking()
            .Where(d => d.UserId == row.UserId && d.PurchaseReference == reference)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync();
    }

    /// <summary>
    /// A switch still waiting for the buyer (not expired). Asking for the same term again returns it; another term is refused while
    /// it can still be paid, except a PayPal approval nobody gave yet, which the new request replaces.
    /// </summary>
    private async Task<SubscriptionTermSwitch> PendingTermSwitch(UserSubscription subscription, int weeks)
    {
        var pending = await LoadPendingTermSwitch(subscription);
        if (pending == null || pending.Weeks == weeks)
            return pending;
        if (!IsPayPal(subscription) || !await AbandonPayPalTermSwitch(pending.NewSubscriptionId))
            throw new ApiException(TermSwitchPending);
        await FailTermSwitch(pending);
        return null;
    }

    private async Task<SubscriptionTermSwitch> LoadPendingTermSwitch(UserSubscription subscription)
    {
        var now = DateTime.UtcNow;
        return await context.SubscriptionTermSwitches.Include(r => r.Product).Include(r => r.RenewalProduct)
            .Where(r => r.SubscriptionId == subscription.ExternalId && r.Status == TermSwitchStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    private async Task<SubscriptionTermSwitchResult> PendingTermSwitchResult(UserSubscription subscription)
    {
        var pending = await LoadPendingTermSwitch(subscription);
        return pending == null ? null
            : TermSwitchResult(subscription, pending, PendingStatus(pending), pending.CreatedAt.AddDays(7 * pending.Weeks));
    }

    private static string PendingStatus(SubscriptionTermSwitch row) => row.PaymentUrl == null ? "processing"
        : row.Provider == SubscriptionProviders.PayPal ? "approval_required" : "payment_required";

    /// <param name="accessUntil">The actual access end once granted; null: the preview (access end at the request plus the term)</param>
    private SubscriptionTermSwitchResult TermSwitchResult(UserSubscription subscription, SubscriptionTermSwitch row, string status,
        DateTime firstRenewalAt, DateTime? accessUntil = null) => new()
    {
        Switched = status == "completed", Status = status,
        ApprovalUrl = status is "payment_required" or "approval_required" ? row.PaymentUrl : null,
        Weeks = row.Weeks, ProductSlug = row.Product.Slug, ChargedNow = MajorUnits(row.ChargedCents, row.Currency),
        Currency = row.Currency, TaxTreatment = PriceTaxTreatment(subscription), PromotionCode = row.PromotionCode,
        AccessUntil = accessUntil ?? row.AccessEndsBefore.AddSeconds(row.Product.OwnershipSeconds), FirstRenewalAt = firstRenewalAt,
        RenewsAsProductSlug = row.RenewalProduct?.Slug, RenewalPrice = MajorUnits(row.RenewalCents, row.Currency),
        RenewalWeeks = SubscriptionRenewal.RenewalWeeks
    };

    /// <summary>
    /// What the payment event of the switch's payment states; <paramref name="paidAt"/> is when billing restarted,
    /// <paramref name="accessEndsAt"/> the actual access end after the grant
    /// </summary>
    private static SubscriptionTermSwitchInfo TermSwitchInfo(SubscriptionTermSwitch row, DateTime paidAt, DateTime? accessEndsAt) => new(
        IntervalWeeks(row.PreviousProduct.OwnershipSeconds) ?? 0, row.AccessEndsBefore,
        accessEndsAt ?? row.AccessEndsBefore.AddSeconds(row.Product.OwnershipSeconds), paidAt.AddDays(7 * row.Weeks), row.PromotionCode);
}
