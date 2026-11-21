using Stripe;

namespace Coflnet.Payments.Services;

/// <summary>
/// Why Stripe refused a charge, without personal data: the decline code, Stripe's outcome and the card's brand, issuing
/// country, funding and 3-D Secure result, and Radar's risk level. The card's last digits, the name and the email are left out.
/// </summary>
public static class StripeChargeFailure
{
    public static string Describe(Charge charge)
    {
        if (charge == null)
            return "no charge";
        var outcome = charge.Outcome;
        var card = charge.PaymentMethodDetails?.Card;
        return $"code={charge.FailureCode ?? "-"} decline={outcome?.Reason ?? "-"} outcome={outcome?.Type ?? "-"}"
            + $" network={outcome?.NetworkStatus ?? "-"} risk={outcome?.RiskLevel ?? "-"} method={charge.PaymentMethodDetails?.Type ?? "-"}"
            + $" card={card?.Brand ?? "-"}/{card?.Country ?? "-"}/{card?.Funding ?? "-"}"
            + $" 3ds={card?.ThreeDSecure?.Result ?? "-"}/{card?.ThreeDSecure?.ResultReason ?? "-"} amount={charge.Amount} {charge.Currency}";
    }
}
