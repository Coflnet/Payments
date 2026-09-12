using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public partial class SubscriptionServiceTests
{
    private Mock<LemonSqueezyService> planProvider;
    private Data providerSubscription;
    private TopUpProduct targetPlan;
    private UserSubscription changingSubscription;
    private TopUpProduct originalPlan;
    private List<SubscriptionInvoice> providerInvoices;

    private async Task SetupPlanSwitch(int slots = 4)
    {
        var owner = await userService.GetOrCreate("plan-owner");
        (await context.Products.SingleAsync(p => p.Slug == "revert")).Type = Product.ProductType.SERVICE;
        var current = originalPlan = await context.TopUpProducts.FirstAsync();
        current.Price = slots == 0 ? 9.69m : 29.69m;
        current.Cost = slots == 0 ? 1800 : 7200;
        current.CurrencyCode = "eur";
        current.OwnershipSeconds = 2419200;
        current.SlotCount = slots;
        current.SlotTier = slots == 0 ? null : "premium";
        targetPlan = new TopUpProduct { Slug = "l_prem_plus-test", Title = "Premium+", Price = 99.69m,
            Cost = 27000, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy", SlotCount = slots,
            SlotTier = slots == 0 ? null : "premium_plus", OwnershipSeconds = 2419200, Type = Product.ProductType.SERVICE };
        context.TopUpProducts.Add(targetPlan);
        foreach (var tier in new[] { "premium", "premium_plus" })
        {
            var product = new PurchaseableProduct { Slug = tier, Title = tier, Type = Product.ProductType.SERVICE };
            context.Products.Add(product);
            await groupService.AddProductToGroup(product, tier);
        }
        await groupService.AddProductToGroup(current, "premium");
        await groupService.AddProductToGroup(targetPlan, "premium_plus");
        await groupService.AddProductToGroup(targetPlan, "premium");
        await groupService.AddProductToGroup(targetPlan, targetPlan.Slug);
        changingSubscription = new UserSubscription { User = owner, Product = current, ExternalId = "40001",
            ProviderVariantId = 201, Status = "active", RenewsAt = DateTime.UtcNow.AddDays(28), UpdatedAt = DateTime.UtcNow };
        context.Subscriptions.Add(changingSubscription);
        await context.SaveChangesAsync();
        await subscriptionService.PaymentReceived(CreatePaymentWebhook(owner.ExternalId, current.Id, "40001"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LEMONSQUEEZY:SUBSCRIPTION_VARIANTS:" + targetPlan.Slug] = "202",
            ["LEMONSQUEEZY:SUBSCRIPTION_VARIANTS:" + current.Slug] = "201"
        }).Build();
        planProvider = new Mock<LemonSqueezyService>(config, NullLogger<LemonSqueezyService>.Instance,
            context, new VariantCacheService(NullLogger<VariantCacheService>.Instance)) { CallBase = true };
        providerInvoices = [];
        providerSubscription = PlanState(201);
        planProvider.Setup(p => p.GetSubscriptionInvoicesAsync("40001")).ReturnsAsync(() => providerInvoices.ToList());
        planProvider.Setup(p => p.GetSubscription("40001")).ReturnsAsync(() => providerSubscription);
        planProvider.Setup(p => p.ValidateSubscriptionPrice(It.IsAny<Data>(), It.IsAny<TopUpProduct>())).ReturnsAsync(true);
        planProvider.Setup(p => p.ValidateSubscriptionVariant(It.IsAny<TopUpProduct>(), It.IsAny<long>())).Returns(Task.CompletedTask);
        planProvider.Setup(p => p.ChangeSubscription("40001", 202, true)).ReturnsAsync(() =>
        {
            AddProrationInvoice();
            return providerSubscription = PlanState(202);
        });
        planProvider.Setup(p => p.ChangeSubscription("40001", 201, false)).ReturnsAsync(() => providerSubscription = PlanState(201));
        planProvider.Setup(p => p.ChangeSubscription("40001", 202, false)).ReturnsAsync(() => providerSubscription = PlanState(202));
        subscriptionService = new SubscriptionService(NullLogger<SubscriptionService>.Instance,
            transactionService, userService, productService, context, planProvider.Object);
    }

    private SubscriptionInvoice AddProrationInvoice(string status = "paid")
    {
        var invoice = new SubscriptionInvoice { Id = "upgrade-" + providerInvoices.Count, SubscriptionId = 40001,
            BillingReason = "updated", Status = status, Total = 5000, CreatedAt = DateTime.UtcNow };
        providerInvoices.Add(invoice);
        return invoice;
    }

    private Data PlanState(long variant, string status = "active", string processor = "stripe", string redirect = null, DateTime? endsAt = null) =>
        JsonSerializer.Deserialize<Webhook>(JsonSerializer.Serialize(new
        {
            data = new { type = "subscriptions", id = "40001", attributes = new
            {
                variant_id = variant, status, payment_processor = processor, ends_at = endsAt,
                renews_at = changingSubscription.RenewsAt, updated_at = DateTime.UtcNow,
                urls = new { customer_portal_update_subscription = redirect }
            } }
        })).Data;

    [Test]
    public async Task SlotUpgradePreservesAssignmentsExpiryAndBalanceAndRetriesWithoutAnotherCharge()
    {
        await SetupPlanSwitch();
        var slots = new TierSlotService(context);
        var before = await slots.GetOwned("plan-owner");
        await slots.Assign("plan-owner", before[0].Id, new() { UserId = "plan-owner", Version = before[0].Version });
        var result = await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        Assert.That(result.Status, Is.EqualTo("completed"));
        var after = await slots.GetOwned("plan-owner");
        Assert.That(after.Select(s => s.Id), Is.EqualTo(before.Select(s => s.Id)));
        Assert.That(after.Select(s => s.Expires), Is.EqualTo(before.Select(s => s.Expires)));
        Assert.That(after.All(s => s.Tier == "premium_plus"), Is.True);
        Assert.That(after[0].AssignedUserId, Is.EqualTo("plan-owner"));
        Assert.That(await context.TierSlotGrants.CountAsync(), Is.EqualTo(4));
        Assert.That((await userService.GetOrCreate("plan-owner")).Balance, Is.Zero);
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        planProvider.Verify(p => p.ChangeSubscription("40001", 202, true), Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DelayedProviderPriceReturnsPendingAndRetryDoesNotChargeAgain(bool upgrade)
    {
        await SetupPlanSwitch(0);
        if (!upgrade)
            await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var previous = changingSubscription.Product;
        var target = upgrade ? targetPlan : originalPlan;
        var expiry = await context.OwnerShips.Select(o => o.Expires).SingleAsync();
        planProvider.SetupSequence(p => p.ValidateSubscriptionPrice(It.IsAny<Data>(), It.IsAny<TopUpProduct>()))
            .ReturnsAsync(false).ReturnsAsync(true);

        var pending = await subscriptionService.ChangePlan("plan-owner", "40001", target.Slug);
        Assert.That(pending.Status, Is.EqualTo("pending"));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(previous.Id));
        Assert.That((await context.OwnerShips.SingleAsync()).Product.Id, Is.EqualTo(previous.Id));
        var completed = await subscriptionService.ChangePlan("plan-owner", "40001", target.Slug);
        Assert.That(completed.Status, Is.EqualTo("completed"));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(target.Id));
        Assert.That((await context.OwnerShips.SingleAsync()).Product.Id, Is.EqualTo(targetPlan.Id));
        Assert.That(await context.OwnerShips.Select(o => o.Expires).SingleAsync(), Is.EqualTo(expiry));
        Assert.That(providerInvoices.Count, Is.EqualTo(1));
        planProvider.Verify(p => p.ChangeSubscription("40001", upgrade ? 202 : 201, upgrade), Times.Once);
    }

    [Test]
    public async Task PersonalUpgradePreservesPrepaidTimeAndUsesExistingPaidPeriod()
    {
        await SetupPlanSwitch(0);
        var before = await context.OwnerShips.Select(o => new { o.Id, o.Expires }).ToArrayAsync();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        Assert.That((await userService.GetAccessUntil("plan-owner", new() { "premium_plus" }))["premium_plus"], Is.EqualTo(before.Single().Expires));
        foreach (var owned in before)
            Assert.That((await context.OwnerShips.FindAsync(owned.Id)).Expires, Is.EqualTo(owned.Expires));
        Assert.That(await context.TierSlots.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ProrationInvoiceAndStaleCheckoutMetadataDoNotRenewOrRestoreOldTier()
    {
        await SetupPlanSwitch();
        var oldProductId = changingSubscription.Product.Id;
        var before = await context.TierSlots.Select(s => s.Expires).ToArrayAsync();
        providerSubscription = PlanState(202);
        AddProrationInvoice();
        var updatedInvoice = CreatePaymentWebhook("plan-owner", oldProductId, "40001", DateTime.UtcNow.AddDays(1));
        updatedInvoice.Data.Attributes.BillingReason = "updated";
        var resolved = await subscriptionService.PaymentReceived(updatedInvoice);
        Assert.That(resolved.CoinAmount, Is.Zero);
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(targetPlan.Id));
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(before));
        Assert.That(await context.FiniteTransactions.CountAsync(), Is.EqualTo(3));

        await subscriptionService.UpdateSubscription(new Webhook(new Meta(false, "subscription_updated",
            new CustomData("plan-owner", oldProductId, 7200, "True")), PlanState(201)));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(targetPlan.Id));
        var renewal = CreatePaymentWebhook("plan-owner", oldProductId, "40001", DateTime.UtcNow.AddDays(28));
        await subscriptionService.PaymentReceived(renewal);
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(before.Select(e => e.AddDays(28))));
        Assert.That(await context.TierSlots.CountAsync(), Is.EqualTo(4));
    }

    [Test]
    public async Task PaypalRedirectWaitsForProviderConfirmation()
    {
        await SetupPlanSwitch();
        providerSubscription = PlanState(201, processor: "paypal");
        planProvider.Setup(p => p.ChangeSubscription("40001", 202, true)).ReturnsAsync(
            PlanState(201, processor: "paypal", redirect: "https://example.lemonsqueezy.com/billing/40001/update"));
        var oldProductId = changingSubscription.Product.Id;
        var result = await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        Assert.That(result.Status, Is.EqualTo("redirect"));
        Assert.That(result.RedirectUrl, Does.StartWith("https://example.lemonsqueezy.com/"));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(oldProductId));
        providerSubscription = PlanState(202, processor: "paypal");
        AddProrationInvoice();
        await subscriptionService.UpdateSubscription(new Webhook(new Meta(false, "subscription_updated", null), providerSubscription));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(targetPlan.Id));
    }

    [Test]
    public async Task FailedUpgradePaymentDoesNotGrantHigherTier()
    {
        await SetupPlanSwitch();
        planProvider.Setup(p => p.ChangeSubscription("40001", 202, true)).ReturnsAsync(PlanState(202, "past_due"));
        var result = await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        Assert.That(result.Status, Is.EqualTo("pending"));
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);
    }

    [Test]
    public async Task UpgradeRejectsOtherOwnerCapacityAndDisabledPlansBeforeProviderCall()
    {
        await SetupPlanSwitch();
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.ChangePlan("other-user", "40001", targetPlan.Slug));
        targetPlan.SlotCount = 1;
        await context.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug));
        targetPlan.SlotCount = 4;
        targetPlan.Type |= Product.ProductType.DISABLED;
        await context.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug));
        planProvider.Verify(p => p.ChangeSubscription(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()), Times.Never);
    }
}
