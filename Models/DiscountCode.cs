using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// A discount code validated locally for providers where Coflnet sets the price itself
/// (Stripe, PayPal, CoinGate). Lemon Squeezy checkouts keep using codes defined in Lemon Squeezy.
/// </summary>
public class DiscountCode
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }
    /// <summary>
    /// The code buyers enter, stored upper case
    /// </summary>
    [Required, MaxLength(50)]
    public string Code { get; set; }
    /// <summary>
    /// Display name shown when the code is validated
    /// </summary>
    [MaxLength(100)]
    public string Name { get; set; }
    /// <summary>
    /// "percent" (Amount is a percentage) or "fixed" (Amount is in cents)
    /// </summary>
    [Required, MaxLength(10)]
    public string AmountType { get; set; } = "percent";
    /// <summary>
    /// Percentage or cents, see <see cref="AmountType"/>
    /// </summary>
    public decimal Amount { get; set; }
    /// <summary>
    /// Optional amounts per billing period in weeks (4, 13 or 52) for subscriptions, e.g. {4:25, 13:50, 52:50}.
    /// A period with an entry uses it instead of <see cref="Amount"/>; other periods keep <see cref="Amount"/>.
    /// Stored as a JSON text column.
    /// </summary>
    public Dictionary<int, decimal> PeriodAmounts { get; set; }
    /// <summary>
    /// Offer this code automatically (e.g. a Black Friday promotion): clients fetch the newest active auto code
    /// and send it with the subscription checkout. Only allowed for <see cref="SubscriptionOnly"/> codes.
    /// </summary>
    public bool AutoApply { get; set; }
    /// <summary>
    /// Inactive codes are never accepted
    /// </summary>
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Only accepted for subscription checkouts.
    /// </summary>
    public bool SubscriptionOnly { get; set; }
    /// <summary>
    /// For subscriptions: <see cref="EveryRenewal"/> (default) lowers every payment,
    /// <see cref="FirstPaymentOnly"/> only the first one (Stripe via a price change after the first invoice, PayPal via a trial cycle priced differently).
    /// </summary>
    [MaxLength(10)]
    public string Duration { get; set; } = EveryRenewal;
    /// <summary>
    /// How often the code may be used; null for unlimited. A use is counted when a checkout is started.
    /// </summary>
    public int? MaxRedemptions { get; set; }
    /// <summary>
    /// Checkouts started with this code
    /// </summary>
    public int TimesRedeemed { get; set; }

    /// <summary>Discount applies to every subscription payment</summary>
    public const string EveryRenewal = "forever";
    /// <summary>Discount applies to the first subscription payment only</summary>
    public const string FirstPaymentOnly = "once";
    /// <summary>
    /// Not accepted before this time
    /// </summary>
    public DateTime? StartsAt { get; set; }
    /// <summary>
    /// Not accepted after this time
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
    /// <summary>
    /// When the code was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The amount that applies to a checkout with the given billing period
    /// </summary>
    public decimal AmountForWeeks(int? weeks) =>
        weeks is int w && PeriodAmounts != null && PeriodAmounts.TryGetValue(w, out var amount) ? amount : Amount;
}
