using System.Globalization;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The terms shown next to the submit button of a Stripe subscription checkout. Kept in one place: the wording is
/// decided by legal review, not by this code.
/// </summary>
public static class StripeSubscriptionDisclosure
{
    /// <summary>Stripe's limit for custom_text.submit.message</summary>
    public const int MaxLength = 1200;
    /// <summary>Where buyers of the default product line (SkyCofl) cancel</summary>
    public const string AccountUrl = "sky.coflnet.com/account";
    /// <summary>Where BazaarPro buyers cancel: its billing settings</summary>
    public const string BazaarProAccountUrl = "pro.skyblock.bz/dashboard/settings/billing";
    /// <summary>SUBSCRIPTIONS:ACCOUNT_URL:{product line} overrides the account url of a line (<see cref="PlanLines"/>)</summary>
    public const string AccountUrlConfigPrefix = "SUBSCRIPTIONS:ACCOUNT_URL:";
    // No submit_type: Stripe refuses submit_type "pay" in subscription mode ("You can not pass `submit_type: 'pay'` in
    // `subscription` mode", live 2026-11-19), so the button keeps Stripe's subscription label.

    /// <summary>{0} weeks, {1} price with currency, {2} account url</summary>
    public const string RenewalTemplate = "Renews every {0} weeks at {1} until cancelled. Cancel anytime in your account at {2}.";
    /// <summary>
    /// 13 and 52 week plans (§ 309 Nr. 9 b BGB): {0} weeks of the first term, {1} renewal weeks, {2} renewal price with
    /// currency, {3} account url
    /// </summary>
    public const string ContinuationTemplate =
        "After the first {0} weeks it continues every {1} weeks at {2} until cancelled. Cancel anytime in your account at {3}.";
    /// <summary>
    /// 13 and 52 week plans: the line item's description, shown under the product name next to Stripe's own "per 52 weeks"
    /// price line: {0} weeks of the first term, {1} renewal weeks, {2} renewal price with currency
    /// </summary>
    public const string LineItemTemplate = "First {0} weeks, then every {1} weeks at {2} until cancelled.";
    /// <summary>
    /// A free trial (<see cref="SubscriptionTrials"/>), before the renewal terms: {0} trial days, {1} trial end and first charge date,
    /// {2} first payment with currency
    /// </summary>
    public const string TrialTemplate =
        "Free for {0} days until {1}; cancel before then and nothing is charged. The first payment of {2} is charged on {1}. ";
    /// <summary>The trial end as written in <see cref="TrialTemplate"/> ("23 November 2026")</summary>
    public const string TrialDateFormat = "d MMMM yyyy";
    public const string DirectSuffix = " The price includes VAT.";

    /// <summary>
    /// The text of custom_text.submit.message of direct sales (Managed Payments refuses custom_text): renewal terms (13 and
    /// 52 week plans: that they continue every 4 weeks at the renewal price). The VAT note is only given where VAT is
    /// charged (a VAT rate is attached; not for the US, rate 0).
    /// </summary>
    public static string SubmitMessage(StripeSubscriptionCheckout checkout) => Trial(checkout) + Renewal(checkout) + Suffix(checkout);

    /// <summary>
    /// The account page where buyers of <paramref name="plan"/> cancel, by its product line: SUBSCRIPTIONS:ACCOUNT_URL:{line}, else
    /// <see cref="BazaarProAccountUrl"/> for BazaarPro and <see cref="AccountUrl"/> for every other line
    /// </summary>
    public static string AccountUrlFor(Product plan, IConfiguration config)
    {
        var line = PlanLines.From(config).LineOf(plan);
        var configured = config?[AccountUrlConfigPrefix + line];
        return string.IsNullOrWhiteSpace(configured) ? DefaultAccountUrl(line) : configured.Trim();
    }

    private static string DefaultAccountUrl(string line) => line == PlanLines.BazaarProLine ? BazaarProAccountUrl : AccountUrl;

    /// <summary>
    /// The trial and the first charge date. The date is the trial end as of the session's creation; a buyer who completes the session
    /// later (it stays open up to 24 hours) gets the full trial from then, so the first charge is never earlier than stated.
    /// The first payment is the plan's price (a trial never comes with a "first payment only" code).
    /// </summary>
    private static string Trial(StripeSubscriptionCheckout checkout)
    {
        if (checkout.TrialDays is not int days || checkout.TrialEndsAt is not System.DateTime end)
            return "";
        return string.Format(CultureInfo.InvariantCulture, TrialTemplate, days, end.ToString(TrialDateFormat, CultureInfo.InvariantCulture),
            Price(checkout.UnitAmountCents, checkout));
    }

    private static string Renewal(StripeSubscriptionCheckout checkout)
    {
        var accountUrl = checkout.AccountUrl ?? AccountUrl;
        if (checkout.RenewalUnitAmountCents is long renewal)
            return string.Format(CultureInfo.InvariantCulture, ContinuationTemplate, checkout.IntervalWeeks,
                SubscriptionRenewal.RenewalWeeks, Price(renewal, checkout), accountUrl);
        return string.Format(CultureInfo.InvariantCulture, RenewalTemplate, checkout.IntervalWeeks,
            Price(checkout.UnitAmountCents, checkout), accountUrl);
    }

    /// <summary>The line item description of a 13 or 52 week plan; null for 4-week plans (they keep the plan's description)</summary>
    public static string LineItemDescription(StripeSubscriptionCheckout checkout) => checkout.RenewalUnitAmountCents is long renewal
        ? string.Format(CultureInfo.InvariantCulture, LineItemTemplate, checkout.IntervalWeeks, SubscriptionRenewal.RenewalWeeks,
            Price(renewal, checkout))
        : null;

    private static string Price(long cents, StripeSubscriptionCheckout checkout) =>
        StripeAmounts.FromMinorUnits(cents, checkout.Plan.CurrencyCode).ToString("0.00", CultureInfo.InvariantCulture) + " "
        + checkout.Plan.CurrencyCode.ToUpperInvariant();

    private static string Suffix(StripeSubscriptionCheckout checkout) => checkout.TaxRateId == null ? "" : DirectSuffix;
}
