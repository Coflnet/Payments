namespace Coflnet.Payments.Models;
#nullable restore

/// <summary>
/// Arguments for creating a new rule
/// </summary>
public class RuleCreate 
{
    /// <summary>
    /// Identifier of this rule
    /// </summary>
    public string Slug { get; set; }
    /// <summary>
    /// In what order to apply the rules (highest first)
    /// </summary>
    public int Priority { get; set; }
    /// <summary>
    /// This rule only applies if the user owns a product contained in this group
    /// if null, the rule always applies to the target group (except overriden by another rule)
    /// </summary>
    public string RequiresGroup { get; set; }
    /// <summary>
    /// This rule applies to all products in this group
    /// </summary>
    public string TargetsGroup { get; set; }
    /// <summary>
    /// Flags specifying what action this rule takes
    /// </summary>
    public Rule.RuleFlags Flags { get; set; }
    /// <summary>
    /// Amount this rule changes the target property
    /// </summary>
    public decimal Amount { get; set; }
}
