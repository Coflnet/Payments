using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Free trials of direct Stripe (direct and Managed Payments) and PayPal subscriptions. Off by default
/// (SUBSCRIPTIONS:DIRECT_TRIAL_ENABLED) and only for the product lines of SUBSCRIPTIONS:TRIAL_LINES (default BazaarPro,
/// <see cref="PlanLines"/>); SkyCofl Premium (the default line) has none. A trial request that does not qualify is ignored, the
/// checkout proceeds without a trial (the caller always asks for one, the checkout response names the trial actually given).
/// One trial per buyer and line: none for a buyer who ever had a trial, a subscription or an ownership of a plan of the line
/// (Lemon Squeezy trials, recorded in <see cref="PaymentContext.TrialUsages"/>, count as well).
/// </summary>
public sealed class SubscriptionTrials
{
    public const string EnabledKey = "SUBSCRIPTIONS:DIRECT_TRIAL_ENABLED";
    /// <summary>The product lines that may have a trial, a list or one comma separated value</summary>
    public const string LinesKey = "SUBSCRIPTIONS:TRIAL_LINES";
    public const string MaxDaysKey = "SUBSCRIPTIONS:TRIAL_MAX_DAYS";
    /// <summary>
    /// Longest trial: card network rules ask for a reminder before longer trials end (Stripe Managed Payments sends one for trials
    /// over 7 days, direct sales would need our own)
    /// </summary>
    public const int DefaultMaxDays = 7;
    /// <summary>Stripe metadata key (checkout and subscription) of the trial days the checkout gave</summary>
    public const string MetadataKey = "trialDays";

    private readonly PaymentContext db;
    private readonly PlanLines planLines;

    public bool Enabled { get; }
    public IReadOnlyList<string> Lines { get; }
    public int MaxDays { get; }

    public SubscriptionTrials(PaymentContext db, IConfiguration config)
    {
        this.db = db;
        planLines = PlanLines.From(config);
        Enabled = string.Equals(config?[EnabledKey], "true", StringComparison.OrdinalIgnoreCase);
        Lines = ReadLines(config);
        MaxDays = int.TryParse(config?[MaxDaysKey], out var days) && days > 0 ? days : DefaultMaxDays;
    }

    /// <summary>
    /// The trial days a new subscription checkout of <paramref name="plan"/> gets, null for none: the request asks for one, trials
    /// are on, the plan (not a slot bundle) is of a trial line, no "first payment only" code applies (it would be spent on the
    /// trial's zero invoice at Stripe, see docs/direct-subscriptions.md "Trials") and the buyer never used the line.
    /// The length is the requested one capped at <see cref="MaxDays"/>.
    /// </summary>
    public async Task<int?> DaysFor(TopUpOptions options, TopUpProduct plan, User user, ValidatedDiscount discount)
    {
        if (options?.EnableTrial != true || !Enabled || plan.SlotCount > 0 || options.TrialLengthDays < 1)
            return null;
        if (discount?.Duration == DiscountCode.FirstPaymentOnly)
            return null;
        var line = planLines.LineOf(await WithGroups(plan));
        if (!Lines.Contains(line) || await HasUsedLine(user, line))
            return null;
        return Math.Min(options.TrialLengthDays, MaxDays);
    }

    /// <summary>Whether the buyer ever had a trial, subscription or ownership of a plan of <paramref name="line"/></summary>
    public async Task<bool> HasUsedLine(User user, string line)
    {
        if (user == null || user.Id == 0)
            return false;
        var productIds = await UsedProductIds(user.Id);
        if (productIds.Count == 0)
            return false;
        var used = await db.Set<Product>().Include(p => p.Groups).AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync();
        return used.Any(p => planLines.LineOf(p) == line);
    }

    private async Task<List<int>> UsedProductIds(int userId)
    {
        var trials = await db.TrialUsages.Where(t => t.UserId == userId).Select(t => t.ProductId).ToListAsync();
        var subscriptions = await db.Subscriptions.Where(s => s.User.Id == userId && s.Product != null)
            .Select(s => s.Product.Id).ToListAsync();
        var ownerships = await db.OwnerShips.Where(o => o.UserId == userId && o.Product != null)
            .Select(o => o.Product.Id).ToListAsync();
        return trials.Concat(subscriptions).Concat(ownerships).Distinct().ToList();
    }

    /// <summary>The checkout loads the plan without its groups; the line also counts by group</summary>
    private async Task<Product> WithGroups(TopUpProduct plan) =>
        await db.Set<Product>().Include(p => p.Groups).AsNoTracking().FirstOrDefaultAsync(p => p.Id == plan.Id) ?? plan;

    private static IReadOnlyList<string> ReadLines(IConfiguration config)
    {
        var section = config?.GetSection(LinesKey);
        if (section == null || !section.Exists())
            return [PlanLines.BazaarProLine];
        var values = section.Value != null
            ? section.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(c => c.Value?.Trim());
        return values.Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
    }
}
