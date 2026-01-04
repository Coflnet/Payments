using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;
public class TopUpOptions
{
    /// <summary>
    /// Overwrite the default redirect url after successful payment
    /// </summary>
    /// <value></value>
    public string SuccessUrl { get; set; }
    /// <summary>
    /// Overwrite the default redirect url for anything else but payment
    /// </summary>
    /// <value></value>
    public string CancelUrl { get; set; }
    /// <summary>
    /// If provided, this value will be used when the Customer object is created. If not provided, customers will be asked to enter their email address
    /// </summary>
    /// <value></value>
    public string UserEmail { get; set; }
    /// <summary>
    /// Percise amount of coflcoins to topup
    /// </summary>
    /// <value></value>
    public long TopUpAmount { get; set; }
    /// <summary>
    /// The ip of the user
    /// </summary>
    [MaxLength(45)]
    public string UserIp { get; set; }
    /// <summary>
    /// Locale of the user
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }
    /// <summary>
    /// Browser fingerprint of the user
    /// </summary>
    [MaxLength(32)]
    public string Fingerprint { get; set; }
    /// <summary>
    /// Optional creator code to apply discount and attribute purchase
    /// </summary>
    [MaxLength(50)]
    public string CreatorCode { get; set; }
    /// <summary>
    /// Optional LemonSqueezy discount code to apply at checkout
    /// </summary>
    [MaxLength(50)]
    public string DiscountCode { get; set; }
    /// <summary>
    /// Whether to enable free trial for subscription. Defaults to false.
    /// Trials are limited to 3 days maximum and can only be used once per user/product.
    /// </summary>
    public bool EnableTrial { get; set; } = false;
    /// <summary>
    /// Trial length in days (1-3). Only used if EnableTrial is true. Defaults to 3.
    /// </summary>
    public int TrialLengthDays { get; set; } = 3;
}