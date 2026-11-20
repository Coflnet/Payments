namespace Coflnet.Payments.Models;

/// <summary>
/// Response containing an id
/// </summary>
public class TopUpIdResponse
{
    /// <summary>
    /// Checkout id
    /// </summary>
    /// <value></value>
    public string Id { get; set; }
    /// <summary>
    /// Directlink to redirect the user to
    /// </summary>
    /// <value></value>
    [Obsolete("Use DirectLink instead")]
    public string DirctLink
    {
        get
        {
            return DirectLink;
        }
        set
        {
            DirectLink = value;
        }
    }
    /// <summary>
    /// Directlink to redirect the user to
    /// </summary>
    public string DirectLink { get; set; }
    /// <summary>
    /// Subscription checkouts: the days of the free trial before the first payment, null without one (also when a trial was asked
    /// for but not given: trials off, a plan without trials, the buyer used the product line before, a "first payment only" code)
    /// </summary>
    public int? TrialDays { get; set; }
}
