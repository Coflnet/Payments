using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// PayPal billing plans per (product slug, billing weeks, currency): created at PayPal on the first checkout and
/// remembered in <see cref="PaymentContext.PayPalPlans"/>. A plan id configured as PAYPAL:SUBSCRIPTION_PLANS:{slug}
/// overrides this (it must have the same TRIAL + REGULAR cycle structure, see <see cref="PayPalSubscriptionApi"/>).
/// </summary>
public class PayPalPlanCatalog
{
    private readonly PaymentContext db;
    private readonly IPayPalSubscriptionApi api;
    private readonly IConfiguration config;

    public PayPalPlanCatalog(PaymentContext db, IPayPalSubscriptionApi api, IConfiguration config)
    {
        this.db = db;
        this.api = api;
        this.config = config;
    }

    /// <summary>
    /// The PayPal plan id for the plan, creating it at PayPal on first use. Concurrent first checkouts may create an
    /// orphan plan at PayPal, but only one plan id is stored and used.
    /// </summary>
    public async Task<string> GetOrCreate(TopUpProduct plan)
    {
        if (ConfiguredPlanId(plan.Slug) is string configured)
            return configured;
        var key = Key(plan);
        var existing = await Find(key);
        if (existing != null)
            return existing.PlanId;
        var created = await CreateAtPayPal(plan, key);
        return await Store(created, key);
    }

    /// <summary>
    /// Whether <paramref name="planId"/> is a plan this service made (or is configured) for the product
    /// </summary>
    public async Task<bool> IsPlanOf(string planId, TopUpProduct plan)
    {
        if (string.IsNullOrEmpty(planId))
            return false;
        if (ConfiguredPlanId(plan.Slug) is string configured)
            return configured == planId;
        var key = Key(plan);
        var row = await Find(key);
        return row?.PlanId == planId;
    }

    /// <summary>
    /// True when the product has a billing period PayPal can be sold with (4, 13 or 52 weeks)
    /// </summary>
    public static bool IsSellable(TopUpProduct plan) =>
        plan != null && SubscriptionService.IntervalWeeks(plan.OwnershipSeconds) != null && !string.IsNullOrEmpty(plan.CurrencyCode);

    private string ConfiguredPlanId(string slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : config?[$"PAYPAL:SUBSCRIPTION_PLANS:{slug}"] is { Length: > 0 } id ? id : null;

    private static (string slug, int weeks, string currency) Key(TopUpProduct plan)
    {
        var weeks = SubscriptionService.IntervalWeeks(plan.OwnershipSeconds)
            ?? throw new ApiException("This plan has no billing period PayPal supports.");
        return (plan.Slug, weeks, plan.CurrencyCode.ToUpperInvariant());
    }

    private Task<PayPalPlan> Find((string slug, int weeks, string currency) key) => db.PayPalPlans.AsNoTracking()
        .FirstOrDefaultAsync(p => p.ProductSlug == key.slug && p.BillingWeeks == key.weeks && p.Currency == key.currency);

    private async Task<PayPalPlan> CreateAtPayPal(TopUpProduct plan, (string slug, int weeks, string currency) key)
    {
        var sibling = await db.PayPalPlans.AsNoTracking().FirstOrDefaultAsync(p => p.ProductSlug == key.slug);
        var catalogProductId = sibling?.PayPalProductId ?? await api.CreateCatalogProduct(plan.Title ?? plan.Slug, plan.Description ?? plan.Title ?? plan.Slug);
        var planId = await api.CreatePlan(new PayPalPlanRequest(catalogProductId, plan.Title ?? plan.Slug,
            plan.Description ?? plan.Title ?? plan.Slug, key.weeks, plan.Price, key.currency));
        return new PayPalPlan
        {
            ProductSlug = key.slug, BillingWeeks = key.weeks, Currency = key.currency, PlanId = planId,
            PayPalProductId = catalogProductId, CreatedAt = DateTime.UtcNow
        };
    }

    private async Task<string> Store(PayPalPlan created, (string slug, int weeks, string currency) key)
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
