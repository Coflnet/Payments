using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public partial class SubscriptionServiceTests
{
    [TestCase("lookup-owner", 2)]
    [TestCase("missing-owner", 0)]
    public async Task SubscriptionLookup_OnlyReturnsTheRequestedOwnersSubscriptions(string userId, int expectedCount)
    {
        var owner = new User { ExternalId = "lookup-owner" };
        var other = new User { ExternalId = "other-owner" };
        var product = await context.TopUpProducts.FirstAsync();
        var now = DateTime.UtcNow;
        context.Subscriptions.AddRange(
            new UserSubscription { User = owner, Product = product, ExternalId = "older", UpdatedAt = now.AddDays(-1) },
            new UserSubscription { User = owner, Product = product, ExternalId = "newer", UpdatedAt = now },
            new UserSubscription { User = other, Product = product, ExternalId = "someone-else", UpdatedAt = now },
            new UserSubscription { Product = product, ExternalId = "orphaned", UpdatedAt = now });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var subscriptions = (await subscriptionService.GetUserSubscriptions(userId)).ToArray();

        Assert.That(subscriptions, Has.Length.EqualTo(expectedCount));
        if (expectedCount > 0)
        {
            Assert.That(subscriptions.Select(s => s.ExternalId), Is.EqualTo(new[] { "newer", "older" }));
            Assert.That(subscriptions.All(s => s.Product?.Id == product.Id), Is.True);
        }
    }
}
