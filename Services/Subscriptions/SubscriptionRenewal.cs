using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// After their first term, 13 and 52 week subscriptions continue as the 4-week plan of the same tier until cancelled
/// (§ 309 Nr. 9 b BGB: an automatic renewal in standard terms may only continue for an indefinite term that can be
/// cancelled with at most one month's notice). This class holds the rules which plan that is.
/// </summary>
public static class SubscriptionRenewal
{
    /// <summary>Billing period of every renewal</summary>
    public const int RenewalWeeks = 4;
    /// <summary>Optional override of the renewal plan: SUBSCRIPTIONS:RENEWAL_PLAN:{slug of the 13/52 week plan} = 4-week slug</summary>
    public const string ConfigPrefix = "SUBSCRIPTIONS:RENEWAL_PLAN:";
    /// <summary>Longest ownership a 4-week plan has (see <see cref="SubscriptionService.SubscriptionInterval"/>)</summary>
    public const long MaxFourWeekSeconds = 2678400;

    /// <summary>
    /// True for plans whose first term is longer than 4 weeks (13 or 52 weeks): they continue as a 4-week plan
    /// </summary>
    public static bool ContinuesAsFourWeeks(Product plan) =>
        plan != null && SubscriptionService.IntervalWeeks(plan.OwnershipSeconds) is int weeks && weeks > RenewalWeeks;

    /// <summary>
    /// Whether <paramref name="candidate"/> can be the 4-week plan <paramref name="plan"/> continues as: a sellable 4-week
    /// subscription plan of the same tier, slot bundle size and currency.
    /// </summary>
    public static bool IsRenewalOf(Product plan, TopUpProduct candidate) =>
        plan != null && candidate != null && candidate.Id != plan.Id
        && SubscriptionService.IsSubscriptionPlan(candidate)
        && SubscriptionService.IntervalWeeks(candidate.OwnershipSeconds) == RenewalWeeks
        && candidate.SlotCount == plan.SlotCount && candidate.SlotTier == plan.SlotTier
        && SubscriptionService.TierRank(candidate) == SubscriptionService.TierRank(plan)
        && string.Equals(candidate.CurrencyCode, (plan as TopUpProduct)?.CurrencyCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The 4-week plan <paramref name="plan"/> continues as: the configured one (when it is a valid renewal plan), else the
    /// only matching plan, else among several the one whose slug the plan's slug starts with (l_premium for l_premium-year).
    /// Null when there is none or the choice is ambiguous.
    /// </summary>
    public static TopUpProduct Select(TopUpProduct plan, IEnumerable<TopUpProduct> candidates, string configuredSlug)
    {
        var matching = candidates.Where(c => IsRenewalOf(plan, c)).ToList();
        if (!string.IsNullOrWhiteSpace(configuredSlug))
            return matching.FirstOrDefault(c => c.Slug == configuredSlug.Trim());
        if (matching.Count <= 1)
            return matching.SingleOrDefault();
        return matching.Where(c => plan.Slug?.StartsWith(c.Slug, StringComparison.Ordinal) == true)
            .OrderByDescending(c => c.Slug.Length).FirstOrDefault();
    }
}

/// <summary>
/// Looks up the 4-week plan a 13 or 52 week plan continues as (<see cref="SubscriptionRenewal"/>)
/// </summary>
public class SubscriptionRenewalPlans
{
    private readonly PaymentContext db;
    private readonly IConfiguration config;

    public SubscriptionRenewalPlans(PaymentContext db, IConfiguration config = null)
    {
        this.db = db;
        this.config = config;
    }

    /// <summary>
    /// The renewal plan of <paramref name="plan"/>; null for plans that bill every 4 weeks anyway and when none is found
    /// </summary>
    public async Task<TopUpProduct> Find(TopUpProduct plan)
    {
        if (!SubscriptionRenewal.ContinuesAsFourWeeks(plan))
            return null;
        var candidates = await db.TopUpProducts.Include(p => p.Groups)
            .Where(p => p.Type.HasFlag(Product.ProductType.SERVICE) && p.SlotCount == plan.SlotCount).ToListAsync();
        // The tier comes from the groups, which callers often did not load (ProductService.GetTopupProduct does not)
        var withGroups = candidates.FirstOrDefault(c => c.Id == plan.Id) ?? plan;
        return SubscriptionRenewal.Select(withGroups, candidates, config?[SubscriptionRenewal.ConfigPrefix + plan.Slug]);
    }

    /// <summary>
    /// Like <see cref="Find"/>, but a 13 or 52 week plan without a renewal plan can not be sold (it could not renew lawfully)
    /// </summary>
    public async Task<TopUpProduct> Require(TopUpProduct plan)
    {
        var renewal = await Find(plan);
        if (renewal == null && SubscriptionRenewal.ContinuesAsFourWeeks(plan))
            throw new ApiException($"The plan {plan.Slug} has no 4-week plan to continue as and can not be sold right now.");
        return renewal;
    }
}
