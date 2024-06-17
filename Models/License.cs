using Newtonsoft.Json;

namespace Coflnet.Payments.Models;
/// <summary>
/// 
/// </summary>
public class License
{
    /// <summary>
    /// Primary Id
    /// </summary>
    /// <value></value>
    public long Id { get; set; }
    /// <summary>
    /// The user having the ownership
    /// </summary>
    /// <value></value>
    [JsonIgnore]
    public User User { get; set; }
    /// <summary>
    /// The produt being owned
    /// </summary>
    /// <value></value>
    public PurchaseableProduct Product { get; set; }
    /// <summary>
    /// How long 
    /// </summary>
    /// <value></value>
    public DateTime Expires { get; set; }
    /// <summary>
    /// The target this individual license is for
    /// </summary>
    public string TargetId { get; set; }
    /// <summary>
    /// The group this license is for
    /// </summary>
    public Group group { get; set; }
    public int? UserId { get; set; }
    public int? ProductId { get; set; }
}

public class PublicLicense
{
    public PublicLicense(License l)
    {
        TargetId = l.TargetId;
        ProductSlug = l.group.Slug;
        Expires = l.Expires;
    }

    public string TargetId { get; set; }
    public string ProductSlug { get; set; }
    public DateTime Expires { get; set; }
}