using System.Collections.Generic;

namespace Coflnet.Payments.Models;

/// <summary>
/// State to get the db into
/// Useful for gitops
/// </summary>
public class SystemState
{
    /// <summary>
    /// Available products
    /// </summary>
    public List<PurchaseableProduct> Products { get; set; }
    /// <summary>
    /// Topups that are currently active
    /// </summary>
    public List<TopUpProduct> TopUpProducts { get; set; }
    /// <summary>
    /// What products go into which groups
    /// </summary>
    public Dictionary<string,string[]> Groups { get; set; }
    /// <summary>
    /// Rules 
    /// </summary>
    public List<RuleCreate> Rules { get; set; }
}