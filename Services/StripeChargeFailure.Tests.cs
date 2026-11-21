using NUnit.Framework;
using Stripe;

namespace Coflnet.Payments.Services;

public class StripeChargeFailureTests
{
    /// <summary>Regression: a declined card payment was logged with its intent id only, so the decline reason was lost</summary>
    [Test]
    public void DescribesTheDeclineWithoutPersonalData()
    {
        var charge = new Charge
        {
            FailureCode = "card_declined",
            Amount = 780,
            Currency = "eur",
            Outcome = new ChargeOutcome { Reason = "do_not_honor", Type = "issuer_declined", NetworkStatus = "declined_by_network", RiskLevel = "normal" },
            BillingDetails = new ChargeBillingDetails { Email = "buyer@example.com", Name = "Buyer Name" },
            PaymentMethodDetails = new ChargePaymentMethodDetails
            {
                Type = "card",
                Card = new ChargePaymentMethodDetailsCard
                {
                    Brand = "visa", Country = "SA", Funding = "debit", Last4 = "4242",
                    ThreeDSecure = new ChargePaymentMethodDetailsCardThreeDSecure { Result = "failed", ResultReason = "rejected" }
                }
            }
        };

        var text = StripeChargeFailure.Describe(charge);

        Assert.That(text, Is.EqualTo("code=card_declined decline=do_not_honor outcome=issuer_declined network=declined_by_network"
            + " risk=normal method=card card=visa/SA/debit 3ds=failed/rejected amount=780 eur"));
        Assert.That(text, Does.Not.Contain("4242").And.Not.Contain("buyer@").And.Not.Contain("Buyer Name"));
    }

    [Test]
    public void ToleratesMissingDetails()
    {
        Assert.That(StripeChargeFailure.Describe(null), Is.EqualTo("no charge"));
        Assert.That(StripeChargeFailure.Describe(new Charge { Amount = 100, Currency = "eur" }),
            Is.EqualTo("code=- decline=- outcome=- network=- risk=- method=- card=-/-/- 3ds=-/- amount=100 eur"));
    }
}
