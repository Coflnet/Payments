namespace Coflnet.Payments.Models;

#nullable enable
/// <summary>
/// Rules can change attributes of products based on the ownership of another
/// </summary>
public class Rule
{
    /// <summary>
    /// Primary key of this rule
    /// </summary>
    /// <value></value>
    public int Id { get; set; }
    /// <summary>
    /// Identifier of this rule
    /// </summary>
    /// <value></value>
    public string Slug { get; set; } = string.Empty;
    /// <summary>
    /// In what order to apply the rules
    /// </summary>
    /// <value></value>
    public int Priority { get; set; }
    /// <summary>
    /// This rule only applies if the user owns a product containing in this group
    /// if null, the rule always applies to the target group (except overriden by another rule)
    /// </summary>
    /// <value></value>
    public Group? Requires { get; set; }
    /// <summary>
    /// This rule applies to all products in this group
    /// </summary>
    /// <value></value>
    public Group Targets { get; set; } = null!;
    /// <summary>
    /// Flags specifying what action this rule takes
    /// </summary>
    /// <value></value>
    public RuleFlags Flags { get; set; }
    /// <summary>
    /// Amount this rule changes the target property
    /// </summary>
    /// <value></value>
    public decimal Amount { get; set; }

    /// <summary>
    /// Flags modifying a <see cref="Rule"/>
    /// </summary>
    [Flags]
    public enum RuleFlags
    {
        /// <summary>
        /// No special handling
        /// </summary>
        NONE,
        /// <summary>
        /// inverts any other flag
        /// </summary>
        INVERT,
        /// <summary>
        /// the default is absolute
        /// </summary>
        PERCENT,
        /// <summary>
        /// change the ownersnip seconds
        /// </summary>
        LONGER = 4,
        /// <summary>
        /// adjust the price 
        /// </summary>
        DISCOUNT = 8,
        /// <summary>
        /// stop checking further 
        /// </summary>
        EARLY_BREAK = 16,
        /// <summary>
        /// the product can't be purchased
        /// </summary>
        BLOCK_PURCHASE = 32
    }
}
