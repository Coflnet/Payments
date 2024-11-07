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
}