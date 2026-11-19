using System;
using Coflnet.Payments.Models;
using NUnit.Framework;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// Purchase confirmations state how tax relates to the paid and the renewal amount (PAngV, Art. 246a EGBGB):
/// the subscription payment event carries it.
/// </summary>
public class SubscriptionEventTaxTests
{
    private const long FourWeeks = 2430000;

    private static TopUpProduct Plan() => new()
    {
        Id = 1, Slug = "l_premium", Title = "Premium", Price = 9.69m, CurrencyCode = "eur", OwnershipSeconds = FourWeeks,
        Type = Product.ProductType.SERVICE, Groups = []
    };

    private static PaymentEvent Apply(bool managed, string country, string currency, decimal amount, decimal tax, double rate,
        long renewalCents, string taxBehavior = null)
    {
        var plan = Plan();
        var subscription = new UserSubscription { Product = plan, PriceCents = renewalCents, Currency = "EUR", ManagedPayments = managed };
        var outcome = new SubscriptionPaymentOutcome(subscription, plan, "in_1", amount, currency, country, null, null, null,
            DateTime.UtcNow, TaxAmount: tax, TaxRate: rate, PriceTaxBehavior: taxBehavior);
        var payment = new PaymentEvent();
        SubscriptionEventTerms.Apply(payment, outcome);
        return payment;
    }

    [Test]
    public void DirectGermanSaleIncludesVatWithRateAndAmount()
    {
        var payment = Apply(false, "DE", "EUR", 11.53m, 1.84m, 19, 1153);

        Assert.That((payment.TaxTreatment, payment.TaxAmount, payment.TaxCountry, payment.VatRateBasisPoints, payment.RenewalTaxTreatment),
            Is.EqualTo((PaymentTaxTreatment.Included, (decimal?)1.84m, "DE", 1900, PaymentTaxTreatment.Included)));
    }

    [Test]
    public void DirectUsSaleChargesNoTax()
    {
        var payment = Apply(false, "US", "EUR", 9.69m, 0, 0, 969);

        Assert.That((payment.TaxTreatment, payment.TaxAmount, payment.TaxCountry, payment.VatRateBasisPoints, payment.RenewalTaxTreatment),
            Is.EqualTo((PaymentTaxTreatment.None, (decimal?)null, "US", 0, PaymentTaxTreatment.None)));
    }

    /// <summary>
    /// Regression: a Managed Payments payment without reported tax (e.g. a US state without tax on digital goods) was marked net,
    /// so the email said "plus applicable taxes" although amount_paid is the whole charge, including any tax Stripe added
    /// </summary>
    [TestCase("exclusive")]
    [TestCase(null)]
    public void ManagedPaymentsPaidAmountIsTheWholeChargeAndTheExclusiveRenewalNet(string taxBehavior)
    {
        var payment = Apply(true, "US", "EUR", 7.27m, 0, 0, 727, taxBehavior);

        Assert.That((payment.TaxTreatment, payment.TaxAmount, payment.RenewalTaxTreatment),
            Is.EqualTo((PaymentTaxTreatment.Included, (decimal?)null, PaymentTaxTreatment.Net)));
        Assert.That((payment.TaxCountry, payment.VatRateBasisPoints), Is.EqualTo(((string)null, 0)), "Stripe decides the tax country and rate");
    }

    [Test]
    public void ManagedPaymentsInclusivePriceRenewsIncludingTax()
    {
        var payment = Apply(true, "FR", "EUR", 7.27m, 1.21m, 0, 727, "inclusive");

        Assert.That(payment.RenewalTaxTreatment, Is.EqualTo(PaymentTaxTreatment.Included));
    }

    /// <summary>Stripe's amount_paid includes the taxes of the invoice; the renewal price stays net</summary>
    [Test]
    public void ManagedPaymentsPaidAmountIncludesTaxStripeReported()
    {
        var payment = Apply(true, "FR", "EUR", 8.72m, 1.45m, 0, 727, "exclusive");

        Assert.That((payment.TaxTreatment, payment.TaxAmount, payment.RenewalTaxTreatment),
            Is.EqualTo((PaymentTaxTreatment.Included, (decimal?)1.45m, PaymentTaxTreatment.Net)));
    }

    [Test]
    public void NoRenewalTaxTreatmentWithoutRenewalAmount()
    {
        var payment = Apply(true, "BR", "BRL", 45.10m, 0, 0, 727);

        Assert.That((payment.SubscriptionRenewalAmount, payment.RenewalTaxTreatment), Is.EqualTo(((decimal?)null, (string)null)));
        Assert.That(payment.TaxTreatment, Is.EqualTo(PaymentTaxTreatment.Included));
    }

    [TestCase(1153L, "EUR", 11.53)]
    [TestCase(1500L, "jpy", 1500)]
    [TestCase(1500L, "KRW", 1500)]
    [TestCase(1000L, "KWD", 1)]
    [TestCase(500L, "ISK", 5)]
    [TestCase(500L, "UGX", 5)]
    [TestCase(969L, null, 9.69)]
    public void StripeAmountsHonourTheCurrencysMinorUnit(long amount, string currency, decimal expected)
    {
        Assert.That(StripeAmounts.FromMinorUnits(amount, currency), Is.EqualTo(expected));
    }
}
