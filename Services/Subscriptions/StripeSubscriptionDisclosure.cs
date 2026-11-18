using System.Globalization;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The terms shown next to the submit button of a Stripe subscription checkout. Kept in one place: the wording is
/// decided by legal review, not by this code.
/// </summary>
public static class StripeSubscriptionDisclosure
{
    /// <summary>Stripe's limit for custom_text.submit.message</summary>
    public const int MaxLength = 1200;
    public const string AccountUrl = "sky.coflnet.com/account";
    /// <summary>
    /// "pay" labels the button "Buy" (the default for subscriptions is "Subscribe"); allowed in subscription mode
    /// by the Checkout Sessions API, also with Managed Payments.
    /// </summary>
    public const string SubmitType = "pay";

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
    public const string DirectSuffix = " The price includes VAT.";
    /// <summary>Stripe's own wording: Managed Payments receipts and statements read "Sold through Link"</summary>
    public const string ManagedSuffix = " The price is before taxes. Sold through Link (Stripe's reseller), which charges any applicable taxes.";

    /// <summary>
    /// The text of custom_text.submit.message: renewal terms (13 and 52 week plans: that they continue every 4 weeks at the
    /// renewal price), for Managed Payments also the reseller notice. The VAT note of
    /// direct sales is only given where VAT is charged (a VAT rate is attached; not for the US, rate 0).
    /// </summary>
    public static string SubmitMessage(StripeSubscriptionCheckout checkout) => Renewal(checkout) + Suffix(checkout);

    private static string Renewal(StripeSubscriptionCheckout checkout)
    {
        if (checkout.RenewalUnitAmountCents is long renewal)
            return string.Format(CultureInfo.InvariantCulture, ContinuationTemplate, checkout.IntervalWeeks,
                SubscriptionRenewal.RenewalWeeks, Price(renewal, checkout), AccountUrl);
        return string.Format(CultureInfo.InvariantCulture, RenewalTemplate, checkout.IntervalWeeks,
            Price(checkout.UnitAmountCents, checkout), AccountUrl);
    }

    /// <summary>The line item description of a 13 or 52 week plan; null for 4-week plans (they keep the plan's description)</summary>
    public static string LineItemDescription(StripeSubscriptionCheckout checkout) => checkout.RenewalUnitAmountCents is long renewal
        ? string.Format(CultureInfo.InvariantCulture, LineItemTemplate, checkout.IntervalWeeks, SubscriptionRenewal.RenewalWeeks,
            Price(renewal, checkout))
        : null;

    private static string Price(long cents, StripeSubscriptionCheckout checkout) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture) + " " + checkout.Plan.CurrencyCode.ToUpperInvariant();

    private static string Suffix(StripeSubscriptionCheckout checkout)
    {
        if (checkout.Mode == StripeCheckoutMode.Managed)
            return ManagedSuffix;
        return checkout.TaxRateId == null ? "" : DirectSuffix;
    }
}
