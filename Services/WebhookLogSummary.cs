using System;
using System.Globalization;
using System.Linq;
using Coflnet.Payments.Models.GooglePay;
using Newtonsoft.Json.Linq;
using Stripe;
using StripeSession = Stripe.Checkout.Session;

namespace Coflnet.Payments.Services;

/// <summary>
/// One-line descriptions of provider webhooks for the logs. Webhook bodies carry personal data (billing name and address,
/// email, payer details) and secrets (purchase tokens), so only non-personal identifiers are logged (GDPR data minimisation):
/// event id and type, object type and id, live mode, state and amount.
/// </summary>
public static class WebhookLogSummary
{
    /// <summary>
    /// e.g. "Stripe event evt_1 invoice.paid invoice in_1 livemode=True amount=969 eur"; amounts in minor units
    /// </summary>
    public static string OfStripe(Event stripeEvent)
    {
        if (stripeEvent == null)
            return "Stripe event (not parsed)";
        var data = stripeEvent.Data?.Object;
        return Join("Stripe event", stripeEvent.Id, stripeEvent.Type, data?.Object, (data as IHasId)?.Id,
            "livemode=" + stripeEvent.Livemode, StripeAmount(data));
    }

    /// <summary>
    /// e.g. "PayPal event WH-1 PAYMENT.SALE.COMPLETED sale SALE-1 state=completed agreement=I-SUB amount=9.69 EUR"
    /// </summary>
    public static string OfPayPal(JObject webhook)
    {
        var resource = webhook?["resource"] as JObject;
        return Join("PayPal event", Text(webhook, "id"), Text(webhook, "event_type"), Text(webhook, "resource_type"),
            Text(resource, "id"), Labeled("state", Text(resource, "state") ?? Text(resource, "status")),
            Labeled("agreement", Text(resource, "billing_agreement_id")), PayPalAmount(resource));
    }

    /// <summary>
    /// e.g. "Google Play notification com.example event=1700000000000 subscription premium type=4"
    /// </summary>
    public static string OfGooglePlay(GooglePlayNotification notification)
    {
        if (notification == null)
            return "Google Play notification (not parsed)";
        return Join("Google Play notification", notification.PackageName, Labeled("event", notification.EventTimeMillis.ToString(CultureInfo.InvariantCulture)),
            GooglePlayKind(notification));
    }

    private static string StripeAmount(IHasObject data) => data switch
    {
        Invoice invoice => Amount(invoice.AmountPaid, invoice.Currency),
        Charge charge => Amount(charge.Amount, charge.Currency),
        PaymentIntent intent => Amount(intent.Amount, intent.Currency),
        StripeSession { AmountTotal: long total } session => Amount(total, session.Currency),
        _ => null
    };

    private static string Amount(long minorUnits, string currency) =>
        Join("amount=" + minorUnits.ToString(CultureInfo.InvariantCulture), currency);

    /// <summary>Sales carry amount.total/currency, captures and refunds amount.value/currency_code</summary>
    private static string PayPalAmount(JObject resource)
    {
        var amount = resource?["amount"] as JObject;
        var value = Text(amount, "total") ?? Text(amount, "value");
        return value == null ? null : Join("amount=" + value, Text(amount, "currency") ?? Text(amount, "currency_code"));
    }

    private static string GooglePlayKind(GooglePlayNotification notification)
    {
        if (notification.SubscriptionNotification is { } subscription)
            return Join("subscription", subscription.SubscriptionId, Labeled("type", subscription.NotificationType.ToString(CultureInfo.InvariantCulture)));
        if (notification.OneTimeProductNotification is { } product)
            return Join("product", product.Sku, Labeled("type", product.NotificationType.ToString(CultureInfo.InvariantCulture)));
        if (notification.VoidedPurchaseNotification is { } voided)
            return Join("voided purchase", Labeled("productType", voided.ProductType.ToString(CultureInfo.InvariantCulture)),
                Labeled("refundType", voided.RefundType.ToString(CultureInfo.InvariantCulture)));
        return notification.TestNotification != null ? "test" : null;
    }

    private static string Text(JObject parent, string key) =>
        parent?[key] is JValue { Value: not null } value ? Convert.ToString(value.Value, CultureInfo.InvariantCulture) : null;

    private static string Labeled(string label, string value) => value == null ? null : label + "=" + value;

    private static string Join(params string[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
}
