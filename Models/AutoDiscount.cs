using System;
using System.Collections.Generic;

namespace Coflnet.Payments.Models;

/// <summary>
/// The promotion clients should offer and send as the discount code of subscription checkouts
/// </summary>
public class AutoDiscount
{
    /// <summary>The code to send with the checkout</summary>
    public string Code { get; set; }
    /// <summary>Display name of the promotion</summary>
    public string Name { get; set; }
    /// <summary>"percent" or "fixed" (cents)</summary>
    public string AmountType { get; set; }
    /// <summary>Default amount, used for periods without an entry in <see cref="PeriodAmounts"/></summary>
    public decimal Amount { get; set; }
    /// <summary>Amounts per billing period in weeks (4, 13, 52); null when every period uses <see cref="Amount"/></summary>
    public Dictionary<int, decimal> PeriodAmounts { get; set; }
    /// <summary>"forever" (every payment) or "once" (first payment only)</summary>
    public string Duration { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    /// <summary>
    /// Subscription providers that honor the code ("stripe", "paypal"). Lemon Squeezy checkouts never do:
    /// do not show the promotion price to buyers routed there.
    /// </summary>
    public List<string> Providers { get; set; }
}
