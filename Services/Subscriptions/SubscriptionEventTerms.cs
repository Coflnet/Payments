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
        payment.SubscriptionBillingWeeks = SubscriptionService.IntervalWeeks(outcome.Product.OwnershipSeconds);
        payment.SubscriptionRenewalWeeks = RenewalWeeks(outcome);
        payment.SubscriptionRenewalAmount = RenewalAmount(outcome);
        payment.SubscriptionIsRenewal = outcome.IsRenewal;
    }

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
        return cents.Value / 100m;
    }
}
