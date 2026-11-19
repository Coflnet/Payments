using Coflnet.Payments.Models.GooglePay;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Stripe;

namespace Coflnet.Payments.Services;

/// <summary>
/// Webhook bodies were logged in full, so a Stripe charge put the buyer's name, street, city, postal code and email into the
/// logs. Only identifiers may be logged.
/// </summary>
public class WebhookLogSummaryTests
{
    private static readonly string[] PersonalData =
        ["Erika Mustermann", "erika@example.test", "Heidestr. 17", "Köln", "51147", "+49 221 1234567"];

    [Test]
    public void StripeChargeIsSummarizedWithoutBillingDetails()
    {
        var charge = new Event
        {
            Id = "evt_1", Type = "charge.succeeded", Livemode = true,
            Data = new EventData
            {
                Object = new Charge
                {
                    Id = "ch_1", Object = "charge", Amount = 969, Currency = "eur", ReceiptEmail = "erika@example.test",
                    BillingDetails = new ChargeBillingDetails
                    {
                        Name = "Erika Mustermann", Email = "erika@example.test", Phone = "+49 221 1234567",
                        Address = new Address { Line1 = "Heidestr. 17", City = "Köln", PostalCode = "51147", Country = "DE" }
                    }
                }
            }
        };

        var summary = WebhookLogSummary.OfStripe(charge);

        Assert.That(summary, Is.EqualTo("Stripe event evt_1 charge.succeeded charge ch_1 livemode=True amount=969 eur"));
        AssertNoPersonalData(summary);
    }

    [Test]
    public void StripeInvoiceAndCheckoutCarryTheirAmountAndNoCustomer()
    {
        var invoice = new Event
        {
            Id = "evt_2", Type = "invoice.paid",
            Data = new EventData
            {
                Object = new Invoice
                {
                    Id = "in_1", Object = "invoice", AmountPaid = 1153, Currency = "eur", CustomerName = "Erika Mustermann",
                    CustomerEmail = "erika@example.test", CustomerAddress = new Address { Line1 = "Heidestr. 17", PostalCode = "51147" }
                }
            }
        };
        var checkout = new Event
        {
            Id = "evt_3", Type = "checkout.session.completed",
            Data = new EventData
            {
                Object = new Stripe.Checkout.Session
                {
                    Id = "cs_1", Object = "checkout.session", AmountTotal = 1153, Currency = "eur",
                    CustomerDetails = new Stripe.Checkout.SessionCustomerDetails { Name = "Erika Mustermann", Email = "erika@example.test" }
                }
            }
        };

        Assert.That(WebhookLogSummary.OfStripe(invoice), Is.EqualTo("Stripe event evt_2 invoice.paid invoice in_1 livemode=False amount=1153 eur"));
        Assert.That(WebhookLogSummary.OfStripe(checkout),
            Is.EqualTo("Stripe event evt_3 checkout.session.completed checkout.session cs_1 livemode=False amount=1153 eur"));
        AssertNoPersonalData(WebhookLogSummary.OfStripe(invoice) + WebhookLogSummary.OfStripe(checkout));
    }

    [Test]
    public void UnparsedStripeEventIsNamedAsSuch()
    {
        Assert.That(WebhookLogSummary.OfStripe(null), Is.EqualTo("Stripe event (not parsed)"));
    }

    [Test]
    public void PayPalSaleIsSummarizedWithoutPayer()
    {
        var webhook = JObject.Parse("""
            {"id":"WH-1","event_type":"PAYMENT.SALE.COMPLETED","resource_type":"sale",
             "resource":{"id":"SALE-1","state":"completed","amount":{"total":"9.69","currency":"EUR"},"billing_agreement_id":"I-SUB",
               "payer":{"name":{"given_name":"Erika","surname":"Mustermann"},"email_address":"erika@example.test",
                 "address":{"address_line_1":"Heidestr. 17","admin_area_2":"Köln","postal_code":"51147"}}}}
            """);

        var summary = WebhookLogSummary.OfPayPal(webhook);

        Assert.That(summary, Is.EqualTo("PayPal event WH-1 PAYMENT.SALE.COMPLETED sale SALE-1 state=completed agreement=I-SUB amount=9.69 EUR"));
        AssertNoPersonalData(summary);
    }

    [Test]
    public void PayPalCaptureUsesItsStatusAndCurrencyCode()
    {
        var webhook = JObject.Parse("""
            {"id":"WH-2","event_type":"PAYMENT.CAPTURE.COMPLETED","resource_type":"capture",
             "resource":{"id":"CAP-1","status":"COMPLETED","amount":{"value":"11.53","currency_code":"EUR"}}}
            """);

        Assert.That(WebhookLogSummary.OfPayPal(webhook),
            Is.EqualTo("PayPal event WH-2 PAYMENT.CAPTURE.COMPLETED capture CAP-1 state=COMPLETED amount=11.53 EUR"));
    }

    [Test]
    public void GooglePlayNotificationLeavesThePurchaseTokenOut()
    {
        var notification = new GooglePlayNotification
        {
            PackageName = "com.example.app", EventTimeMillis = 1700000000000,
            SubscriptionNotification = new SubscriptionNotification
                { NotificationType = 4, SubscriptionId = "premium_monthly", PurchaseToken = "secret-purchase-token" }
        };

        var summary = WebhookLogSummary.OfGooglePlay(notification);

        Assert.That(summary, Is.EqualTo("Google Play notification com.example.app event=1700000000000 subscription premium_monthly type=4"));
        Assert.That(summary, Does.Not.Contain("secret-purchase-token"));
    }

    private static void AssertNoPersonalData(string summary)
    {
        foreach (var value in PersonalData)
            Assert.That(summary, Does.Not.Contain(value));
    }
}
