using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Owner decision: a product sold through this service for another product (BazaarPro) may be in Coflnet's tier groups (so it grants
/// Premium) but stays its own product line. A subscription only switches plans or terms, and only renews, within its line, so a
/// Premium subscriber is never offered BazaarPro and a BazaarPro subscriber never Premium+ (which would drop BazaarPro).
/// A product's line is the first entry of SUBSCRIPTIONS:SEPARATE_PLAN_GROUPS (default <c>l_bazaarpro</c>) that its slug starts with
/// or that is the slug of one of its groups; every other product is in the default line.
/// </summary>
public sealed class PlanLines
{
    /// <summary>The separate lines: their group slugs / slug prefixes, a list or one comma separated value</summary>
    public const string ConfigKey = "SUBSCRIPTIONS:SEPARATE_PLAN_GROUPS";
    /// <summary>The line of every product in none of the separate groups</summary>
    public const string DefaultLine = "";
    /// <summary>The lines without configuration: BazaarPro and the default line</summary>
    public static readonly PlanLines Default = new(["l_bazaarpro"]);

    private readonly IReadOnlyList<string> separate;

    /// <param name="separateGroups">Group slugs / slug prefixes of the separate lines, the first match wins; empty: one line</param>
    public PlanLines(IEnumerable<string> separateGroups)
    {
        separate = separateGroups?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct().ToList() ?? [];
    }

    /// <summary>The configured separate groups/slug prefixes, in order</summary>
    public IReadOnlyList<string> SeparateGroups => separate;

    /// <summary>
    /// The lines of SUBSCRIPTIONS:SEPARATE_PLAN_GROUPS: a list (SUBSCRIPTIONS__SEPARATE_PLAN_GROUPS__0=...) or one comma separated
    /// value; <see cref="Default"/> when it is not set, none when it is set but empty
    /// </summary>
    public static PlanLines From(IConfiguration config)
    {
        var section = config?.GetSection(ConfigKey);
        if (section == null || !section.Exists())
            return Default;
        if (section.Value != null)
            return new(section.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return new(section.GetChildren().Select(c => c.Value));
    }

    /// <summary>The line of <paramref name="product"/>: the first separate group it belongs to, else <see cref="DefaultLine"/></summary>
    public string LineOf(Product product) =>
        product == null ? DefaultLine : separate.FirstOrDefault(line => BelongsTo(product, line)) ?? DefaultLine;

    /// <summary>Whether a subscription may switch or renew from <paramref name="a"/> to <paramref name="b"/></summary>
    public bool SameLine(Product a, Product b) => LineOf(a) == LineOf(b);

    private static bool BelongsTo(Product product, string line) =>
        product.Slug?.StartsWith(line, StringComparison.Ordinal) == true
        || product.Groups?.Any(g => g.Slug == line) == true;
}
