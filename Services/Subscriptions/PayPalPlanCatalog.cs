using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// PayPal billing plans per (product slug, billing weeks, renewal weeks, currency): created at PayPal on the first checkout
/// and remembered in <see cref="PaymentContext.PayPalPlans"/>. Plans are a TRIAL cycle of the plan's period followed by
/// REGULAR cycles of 4 weeks (13 and 52 week plans continue every 4 weeks, <see cref="SubscriptionRenewal"/>); the 13 and 52
/// week plans created before that repeat their period and are only still accepted for subscriptions made on them.
/// A plan id configured as PAYPAL:SUBSCRIPTION_PLANS:{slug} overrides this for 4-week plans (same TRIAL + REGULAR structure,
/// see <see cref="PayPalSubscriptionApi"/>). It is ignored for 13 and 52 week plans (warning logged once): nothing proves such
/// a plan continues every 4 weeks, and one that repeats its period would renew unlawfully.
/// Free trials (<see cref="SubscriptionTrials"/>) use separate plans that start with a free TRIAL cycle of the trial days. They are
/// stored under the product slug plus <see cref="TrialSuffix"/> and the days (<c>l_bazaarpro+trial3d</c>), so no plan made before is
/// changed or reused for them, no schema change is needed and a release without trials never picks one (it looks up the plain slug).
/// </summary>
public class PayPalPlanCatalog
{
    private readonly PaymentContext db;
    private readonly IPayPalSubscriptionApi api;
    private readonly IConfiguration config;
    private readonly ILogger<PayPalPlanCatalog> logger;
    private static readonly ConcurrentDictionary<string, bool> WarnedOverrides = new();
    /// <summary>Marks the stored slug of a plan with a free trial: {slug}+trial{days}d</summary>
    public const string TrialSuffix = "+trial";

    public PayPalPlanCatalog(PaymentContext db, IPayPalSubscriptionApi api, IConfiguration config, ILogger<PayPalPlanCatalog> logger = null)
    {
        this.db = db;
        this.api = api;
        this.config = config;
        this.logger = logger;
    }

    /// <summary>
    /// The PayPal plan id for the plan, creating it at PayPal on first use. <paramref name="renewalPlan"/> is the 4-week plan a
    /// 13 or 52 week plan continues as (required for those; its list price is the REGULAR cycle's default, subscriptions
    /// override it). Concurrent first checkouts may create an orphan plan at PayPal, but only one plan id is stored and used.
    /// </summary>
    /// <param name="trialDays">The plan variant with a free trial of this many days (never the configured override); null: none</param>
    public async Task<string> GetOrCreate(TopUpProduct plan, TopUpProduct renewalPlan = null, int? trialDays = null)
    {
        if (trialDays is not > 0 && ConfiguredPlanId(plan) is string configured)
            return configured;
        var key = Key(plan, renewalPlan, trialDays);
        var existing = await Find(key);
        if (existing != null)
            return existing.PlanId;
        var created = await CreateAtPayPal(plan, renewalPlan, key, trialDays);
        return await Store(created, key);
    }

    /// <summary>The free trial days <paramref name="planId"/> starts with, null for a plan without one (or no plan of the product)</summary>
    public async Task<int?> TrialDays(string planId, TopUpProduct plan) => TrialDaysOf((await PlanOf(planId, plan))?.ProductSlug, plan.Slug);

    /// <summary>The days of a stored trial variant slug ({slug}+trial{days}d), null for the plain slug</summary>
    internal static int? TrialDaysOf(string storedSlug, string productSlug)
    {
        var prefix = productSlug + TrialSuffix;
        if (storedSlug == null || !storedSlug.StartsWith(prefix, StringComparison.Ordinal) || !storedSlug.EndsWith('d'))
            return null;
        return int.TryParse(storedSlug[prefix.Length..^1], out var days) && days > 0 ? days : null;
    }

    private static string StoredSlug(string productSlug, int? trialDays) =>
        trialDays > 0 ? $"{productSlug}{TrialSuffix}{trialDays}d" : productSlug;

    /// <summary>
    /// Whether <paramref name="planId"/> is a plan this service made (or is configured) for the product, including the plans
    /// made before the 4-week renewal (subscriptions on them keep working)
    /// </summary>
    public async Task<bool> IsPlanOf(string planId, TopUpProduct plan) => await PlanOf(planId, plan) != null;

    /// <summary>
    /// The period in weeks of the REGULAR cycles of <paramref name="planId"/>, null when it is no plan of the product.
    /// A configured plan is required to continue every 4 weeks.
    /// </summary>
    public async Task<int?> RenewalWeeks(string planId, TopUpProduct plan) => (await PlanOf(planId, plan))?.RenewalWeeks;

    /// <summary>
    /// True when the product has a billing period PayPal can be sold with (4, 13 or 52 weeks)
    /// </summary>
    public static bool IsSellable(TopUpProduct plan) =>
        plan != null && SubscriptionService.IntervalWeeks(plan.OwnershipSeconds) != null && !string.IsNullOrEmpty(plan.CurrencyCode);

    /// <summary>
    /// The stored plan (or the configured override) <paramref name="planId"/> of the product; its trial variants count as well. With an
    /// override, only it and the trial variants (never made from it) are the product's plans.
    /// </summary>
    private async Task<PayPalPlan> PlanOf(string planId, TopUpProduct plan)
    {
        if (string.IsNullOrEmpty(planId))
            return null;
        var configured = ConfiguredPlanId(plan);
        if (configured == planId)
            return new PayPalPlan { PlanId = planId, ProductSlug = plan.Slug, RenewalWeeks = SubscriptionRenewal.RenewalWeeks };
        var (slug, weeks, currency) = ProductKey(plan);
        var trialPrefix = slug + TrialSuffix;
        var stored = await db.PayPalPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == planId
            && (p.ProductSlug == slug || p.ProductSlug.StartsWith(trialPrefix)) && p.BillingWeeks == weeks && p.Currency == currency);
        if (configured != null && TrialDaysOf(stored?.ProductSlug, slug) == null)
            return null;
        return stored;
    }

    private string ConfiguredPlanId(TopUpProduct plan)
    {
        var id = string.IsNullOrWhiteSpace(plan.Slug) ? null : config?[$"PAYPAL:SUBSCRIPTION_PLANS:{plan.Slug}"];
        if (string.IsNullOrEmpty(id))
            return null;
        if (!SubscriptionRenewal.ContinuesAsFourWeeks(plan))
            return id;
        if (WarnedOverrides.TryAdd(plan.Slug, true))
            logger?.LogWarning("PAYPAL:SUBSCRIPTION_PLANS:{Slug} ({PlanId}) is ignored: 13 and 52 week plans use the created plans that continue every 4 weeks",
                plan.Slug, id);
        return null;
    }

    private static (string slug, int weeks, string currency) ProductKey(TopUpProduct plan)
    {
        var weeks = SubscriptionService.IntervalWeeks(plan.OwnershipSeconds)
            ?? throw new ApiException("This plan has no billing period PayPal supports.");
        return (plan.Slug, weeks, plan.CurrencyCode.ToUpperInvariant());
    }

    /// <summary>Every plan made now renews every 4 weeks (4-week plans with their own period); the slug is the stored one</summary>
    private static (string slug, int weeks, int renewalWeeks, string currency) Key(TopUpProduct plan, TopUpProduct renewalPlan, int? trialDays)
    {
        var (slug, weeks, currency) = ProductKey(plan);
        if (SubscriptionRenewal.ContinuesAsFourWeeks(plan) && renewalPlan == null)
            throw new ApiException($"The plan {plan.Slug} has no 4-week plan to continue as.");
        return (StoredSlug(slug, trialDays), weeks, SubscriptionRenewal.RenewalWeeks, currency);
    }

    private Task<PayPalPlan> Find((string slug, int weeks, int renewalWeeks, string currency) key) => db.PayPalPlans.AsNoTracking()
        .FirstOrDefaultAsync(p => p.ProductSlug == key.slug && p.BillingWeeks == key.weeks && p.RenewalWeeks == key.renewalWeeks
            && p.Currency == key.currency);

    private async Task<PayPalPlan> CreateAtPayPal(TopUpProduct plan, TopUpProduct renewalPlan,
        (string slug, int weeks, int renewalWeeks, string currency) key, int? trialDays)
    {
        var sibling = await db.PayPalPlans.AsNoTracking().FirstOrDefaultAsync(p => p.ProductSlug == plan.Slug)
            ?? await db.PayPalPlans.AsNoTracking().FirstOrDefaultAsync(p => p.ProductSlug == key.slug);
        var catalogProductId = sibling?.PayPalProductId ?? await api.CreateCatalogProduct(plan.Title ?? plan.Slug, plan.Description ?? plan.Title ?? plan.Slug);
        var planId = await api.CreatePlan(new PayPalPlanRequest(catalogProductId, plan.Title ?? plan.Slug,
            plan.Description ?? plan.Title ?? plan.Slug, key.weeks, plan.Price, key.currency,
            key.renewalWeeks, (renewalPlan ?? plan).Price, trialDays > 0 ? trialDays : null));
        return new PayPalPlan
        {
            ProductSlug = key.slug, BillingWeeks = key.weeks, RenewalWeeks = key.renewalWeeks, Currency = key.currency,
            PlanId = planId, PayPalProductId = catalogProductId, CreatedAt = DateTime.UtcNow
        };
    }

    private async Task<string> Store(PayPalPlan created, (string slug, int weeks, int renewalWeeks, string currency) key)
    {
        db.PayPalPlans.Add(created);
        try
        {
            await db.SaveChangesAsync();
            return created.PlanId;
        }
        catch (DbUpdateException)
        {
            // A concurrent first checkout stored its plan first; use that one (ours stays an unused orphan at PayPal).
            db.Entry(created).State = EntityState.Detached;
            var winner = await Find(key);
            return winner?.PlanId ?? throw new ApiException("The PayPal plan could not be stored. Please try again.");
        }
    }
}
