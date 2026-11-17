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

    /// <summary>{0} weeks, {1} price with currency</summary>
    public const string RenewalTemplate = "Renews every {0} weeks at {1} until cancelled. Cancel anytime in your account at {2}.";
    public const string DirectSuffix = " The price includes VAT.";
    /// <summary>Stripe's own wording: Managed Payments receipts and statements read "Sold through Link"</summary>
    public const string ManagedSuffix = " The price is before taxes. Sold through Link (Stripe's reseller), which charges any applicable taxes.";

    /// <summary>
    /// The text of custom_text.submit.message: renewal terms, for Managed Payments also the reseller notice. The VAT note of
    /// direct sales is only given where VAT is charged (a VAT rate is attached; not for the US, rate 0).
    /// </summary>
    public static string SubmitMessage(StripeSubscriptionCheckout checkout)
    {
        var price = (checkout.UnitAmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)
            + " " + checkout.Plan.CurrencyCode.ToUpperInvariant();
        var message = string.Format(CultureInfo.InvariantCulture, RenewalTemplate, checkout.IntervalWeeks, price, AccountUrl);
        return message + Suffix(checkout);
    }

    private static string Suffix(StripeSubscriptionCheckout checkout)
    {
        if (checkout.Mode == StripeCheckoutMode.Managed)
            return ManagedSuffix;
        return checkout.TaxRateId == null ? "" : DirectSuffix;
    }
}
