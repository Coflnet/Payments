using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class GooglePlayEligibilityTests : DirectSubscriptionTestBase
{
    [TestCase("googleplay", "pending", false, false)]
    [TestCase("googleplay", "pending", true, true)]
    [TestCase("googleplay", "active", false, true)]
    [TestCase("stripe", "pending", false, true)]
    public async Task OnlyPendingPlayWithoutAGrantedPeriodDoesNotBlock(string provider, string status, bool granted, bool blocks)
    {
        var buyer = await users.GetOrCreate("buyer");
        var subscription = new UserSubscription
        {
            User = buyer, Product = premium, ProviderSlug = provider, ExternalId = "subscription", Status = status
        };
        db.Subscriptions.Add(subscription);
        if (granted)
            db.FiniteTransactions.Add(new FiniteTransaction
            {
                User = buyer, Product = premium, SubscriptionId = subscription.ExternalId, Amount = -premium.Cost,
                Reference = "googleplay-order-paid"
            });
        await db.SaveChangesAsync();

        var eligibilityBlocker = await SubscriptionService.ActiveRenewingInLine(db, PlanLines.Default, "buyer", premium);
        var webBlocker = await SubscriptionService.ActiveRenewingInLine(db, PlanLines.Default, "buyer", premium, SubscriptionProviders.GooglePlay);

        Assert.That(eligibilityBlocker != null, Is.EqualTo(blocks));
        Assert.That(webBlocker != null, Is.EqualTo(blocks && provider == SubscriptionProviders.GooglePlay));
    }
}
