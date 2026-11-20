using System;
using Coflnet.Payments.Models;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The subscription terms a direct or Managed Payments payment event carries (purchase confirmation emails read them)
/// </summary>
public static class SubscriptionEventTerms
{
    public static void Apply(PaymentEvent payment, SubscriptionPaymentOutcome outcome)
    {
        payment.ProductTitle = outcome.Product.Title;
        payment.SubscriptionBillingWeeks = SubscriptionService.IntervalWeeks(outcome.Product.OwnershipSeconds);
        payment.SubscriptionRenewalWeeks = RenewalWeeks(outcome);
        payment.SubscriptionRenewalAmount = RenewalAmount(outcome);
        payment.SubscriptionIsRenewal = outcome.IsRenewal;
        payment.SubscriptionIsExtension = outcome.IsExtension ? true : null;
        payment.SubscriptionExternalId = outcome.Subscription.ExternalId;
        payment.LegalLocale = outcome.LegalLocale;
        ApplyTax(payment, outcome);
        ApplyDeclaration(payment, outcome);
        ApplyTermSwitch(payment, outcome.TermSwitch);
    }

    /// <summary>
    /// A switch to a longer term is confirmed like the purchase of that term, stating that the paid days left were kept: the service
    /// then ends at the new access end (the previous access end plus the new term), not at the provider's next billing date
    /// </summary>
    private static void ApplyTermSwitch(PaymentEvent payment, SubscriptionTermSwitchInfo termSwitch)
    {
        if (termSwitch == null)
            return;
        payment.SubscriptionIsTermSwitch = true;
        payment.SubscriptionPreviousBillingWeeks = termSwitch.PreviousWeeks;
        payment.SubscriptionPreviousAccessEndsAt = termSwitch.PreviousAccessEndsAt;
        payment.SubscriptionNextBillingAt = termSwitch.NextBillingAt;
        payment.SubscriptionPromotionCode = termSwitch.PromotionCode;
        if (payment.ServiceEndsAtUtc != null)
            payment.ServiceEndsAtUtc = termSwitch.AccessEndsAt;
    }

    /// <summary>
    /// The confirmation of a purchase (first payment, first payment of an extension term) repeats the buyer's early performance
    /// declaration and the term it covers: from the payment to the end of the paid term. It also names the agreement the buyer accepted
    /// (a BazaarPro checkout its own terms), like the confirmation of a prepaid purchase; without one the broker attaches SkyCofl's terms.
    /// </summary>
    private static void ApplyDeclaration(PaymentEvent payment, SubscriptionPaymentOutcome outcome)
    {
        var declaration = outcome.Declaration;
        if (declaration == null)
            return;
        payment.DeclarationVersion = declaration.DeclarationVersion;
        payment.DeclarationText = declaration.DeclarationText;
        payment.AgreementId = declaration.AgreementId;
        payment.AgreementHash = declaration.AgreementHash;
        payment.LegalLocale = declaration.Locale;
        payment.ServiceStartsAtUtc = outcome.PaidAt;
        payment.ServiceEndsAtUtc = PaidTermEnd(outcome);
    }

    /// <summary>The provider's period end once it moved past the payment, else the plan's length from the payment</summary>
    private static DateTime PaidTermEnd(SubscriptionPaymentOutcome outcome)
    {
        var renewsAt = outcome.Subscription.RenewsAt;
        return renewsAt > outcome.PaidAt.AddDays(1) ? renewsAt : outcome.PaidAt.AddSeconds(outcome.Product.OwnershipSeconds);
    }

    /// <summary>
    /// How tax relates to a price of a subscription (renewal or extension price): direct sales by the VAT rate of the checkout
    /// country, Managed Payments by the Stripe price's tax behaviour (null: net)
    /// </summary>
    public static string PriceTaxTreatment(bool managedPayments, string managedTaxBehavior, int vatBasisPoints) =>
        managedPayments ? ManagedPriceTaxTreatment(managedTaxBehavior)
            : vatBasisPoints > 0 ? PaymentTaxTreatment.Included : PaymentTaxTreatment.None;

    /// <summary>
    /// How tax relates to the paid and the renewal amount. Direct sales charge the VAT of the checkout country (none in the US).
    /// Managed Payments: the paid amount is the invoice's amount_paid, the whole charge including any tax Stripe added (with
    /// <see cref="PaymentEvent.TaxAmount"/> when Stripe reports it); the renewal price follows the Stripe price's tax behaviour.
    /// </summary>
    private static void ApplyTax(PaymentEvent payment, SubscriptionPaymentOutcome outcome)
    {
        payment.TaxTreatment = PaidTaxTreatment(outcome);
        payment.TaxAmount = payment.TaxTreatment == PaymentTaxTreatment.Included && outcome.TaxAmount > 0 ? outcome.TaxAmount : null;
        payment.RenewalTaxTreatment = payment.SubscriptionRenewalAmount == null ? null : RenewalTaxTreatment(outcome);
        if (outcome.TaxRemittedByProcessor)
            return;
        payment.TaxCountry = outcome.Country;
        payment.VatRateBasisPoints = (int)Math.Round(outcome.TaxRate * 100);
    }

    private static string PaidTaxTreatment(SubscriptionPaymentOutcome outcome) =>
        outcome.TaxRemittedByProcessor ? PaymentTaxTreatment.Included : DirectTaxTreatment(outcome);

    private static string RenewalTaxTreatment(SubscriptionPaymentOutcome outcome) =>
        outcome.TaxRemittedByProcessor ? ManagedPriceTaxTreatment(outcome.PriceTaxBehavior) : DirectTaxTreatment(outcome);

    /// <summary>
    /// Managed Payments prices: "inclusive" contains the tax; "exclusive" (STRIPE:MANAGED_TAX_BEHAVIOR, the default) is net,
    /// and so is an unknown behaviour (the checkout default)
    /// </summary>
    private static string ManagedPriceTaxTreatment(string taxBehavior) =>
        taxBehavior == "inclusive" ? PaymentTaxTreatment.Included : PaymentTaxTreatment.Net;

    /// <summary>Direct sales: the price contains the checkout country's VAT, or no tax at rate 0 (the US)</summary>
    private static string DirectTaxTreatment(SubscriptionPaymentOutcome outcome) =>
        outcome.TaxRate > 0 ? PaymentTaxTreatment.Included : PaymentTaxTreatment.None;

    /// <summary>4 for 4-week plans and 13/52 week ones that continue every 4 weeks; a plan's own weeks when it repeats its term</summary>
    private static int? RenewalWeeks(SubscriptionPaymentOutcome outcome) =>
        SubscriptionRenewal.ContinuesAsFourWeeks(outcome.Product) && outcome.Subscription.RenewalProduct == null
            ? SubscriptionService.IntervalWeeks(outcome.Product.OwnershipSeconds)
            : SubscriptionRenewal.RenewalWeeks;

    /// <summary>
    /// The renewal price as agreed (direct: gross, Managed Payments: net), only when the payment is in the agreed currency
    /// </summary>
    private static decimal? RenewalAmount(SubscriptionPaymentOutcome outcome)
    {
        var subscription = outcome.Subscription;
        var cents = SubscriptionService.InFirstLongTerm(subscription) ? subscription.RenewalPriceCents : subscription.PriceCents;
        if (cents == null || !string.Equals(outcome.Currency, subscription.Currency, StringComparison.OrdinalIgnoreCase))
            return null;
        return StripeAmounts.FromMinorUnits(cents.Value, subscription.Currency);
    }
}
