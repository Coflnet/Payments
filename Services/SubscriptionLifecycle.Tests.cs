using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Payments.Controllers;

namespace Coflnet.Payments.Services;

public partial class SubscriptionServiceTests
{
    [TestCase("subscription_updated")]
    [TestCase("subscription_plan_changed")]
    public async Task SignedPlanWebhookWaitsForPaidInvoiceAndDoesNotRenewAccess(string eventName)
    {
        await SetupPlanSwitch();
        var expiry = await context.TierSlots.Select(s => s.Expires).ToArrayAsync();
        providerSubscription = PlanState(202);
        var invoice = AddProrationInvoice("pending");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Webhook(new Meta(false, eventName, null), providerSubscription));
        const string secret = "webhook-test-secret";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LEMONSQUEEZY:SECRET"] = secret
        }).Build();
        var controller = new CallbackController(config, NullLogger<CallbackController>.Instance,
            context, transactionService, null, null, subscriptionService,
            NullLogger<GooglePayController>.Instance, null, productService, null, null)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
        controller.Request.Body = new MemoryStream(payload);
        Assert.That(await controller.LemonSqueezy(signature), Is.TypeOf<OkResult>());
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);

        invoice.Status = "paid";
        controller.Request.Body = new MemoryStream(payload);
        Assert.That(await controller.LemonSqueezy(signature), Is.TypeOf<OkResult>());
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium_plus"), Is.True);
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(expiry));
        Assert.That(changingSubscription.User.Balance, Is.Zero);
    }

    private Webhook RefundInvoice(string id, string reason, bool full = true, DateTime? createdAt = null) =>
        JsonSerializer.Deserialize<Webhook>(JsonSerializer.Serialize(new
        {
            meta = new { event_name = "subscription_payment_refunded" },
            data = new { type = "subscription-invoices", id, attributes = new
            {
                subscription_id = 40001, billing_reason = reason, status = full ? "refunded" : "partial_refund",
                refunded = full, refunded_amount = full ? 5000 : 1000, total = 5000,
                created_at = createdAt ?? DateTime.UtcNow, updated_at = DateTime.UtcNow
            } }
        }));

    [TestCase(0)]
    [TestCase(4)]
    public async Task DowngradeKeepsPaidTierUntilRenewalAndPreservesUnrelatedPurchases(int slots)
    {
        await SetupPlanSwitch(slots);
        var unrelated = new OwnerShip { User = changingSubscription.User, Product = targetPlan, Expires = DateTime.UtcNow.AddDays(60) };
        context.OwnerShips.Add(unrelated);
        await context.SaveChangesAsync();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        await subscriptionService.ChangePlan("plan-owner", "40001", originalPlan.Slug);
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(originalPlan.Id));
        if (slots > 0)
            Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium_plus"), Is.True);
        else
            Assert.That((await context.OwnerShips.SingleAsync(o => o.SubscriptionId == "40001")).Product.Id, Is.EqualTo(targetPlan.Id));
        var before = unrelated.Expires;
        var renewal = CreatePaymentWebhook("plan-owner", originalPlan.Id, "40001", DateTime.UtcNow.AddDays(28));
        renewal.Data.Attributes.BillingReason = "renewal";
        await subscriptionService.PaymentReceived(renewal);
        if (slots > 0)
            Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);
        else
            Assert.That((await context.OwnerShips.SingleAsync(o => o.SubscriptionId == "40001")).Product.Id, Is.EqualTo(originalPlan.Id));
        Assert.That(unrelated.Expires, Is.EqualTo(before));
        Assert.That(changingSubscription.User.Balance, Is.Zero);
        planProvider.Verify(p => p.ChangeSubscription("40001", 201, false), Times.Once);
    }

    [Test]
    public async Task UndoingScheduledDowngradeDoesNotChargeForAlreadyPaidTier()
    {
        await SetupPlanSwitch();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        await subscriptionService.ChangePlan("plan-owner", "40001", originalPlan.Slug);
        Assert.That((await subscriptionService.GetAvailablePlans("plan-owner", "40001")).Single().IsUpgrade, Is.False);
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        planProvider.Verify(p => p.ChangeSubscription("40001", 202, true), Times.Once);
        planProvider.Verify(p => p.ChangeSubscription("40001", 202, false), Times.Once);
    }

    [TestCase(0)]
    [TestCase(4)]
    [TestCase(1)]
    public async Task CancellingAndResumingPreservePaidPeriodAndClearCancellation(int slots)
    {
        await SetupPlanSwitch(slots);
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var expiry = slots > 0 ? await context.TierSlots.Select(s => s.Expires).FirstAsync()
            : (await context.OwnerShips.SingleAsync()).Expires;
        planProvider.Setup(p => p.CancelSubscription("40001")).ReturnsAsync(PlanState(202, "cancelled", endsAt: changingSubscription.RenewsAt));
        planProvider.Setup(p => p.ResumeSubscription("40001")).ReturnsAsync(PlanState(202));
        await subscriptionService.CancelSubscription("plan-owner", "40001");
        Assert.That(changingSubscription.Status, Is.EqualTo("cancelled"));
        Assert.That(await subscriptionService.GetAvailablePlans("plan-owner", "40001"), Is.Empty);
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.CancelSubscription("someone-else", "40001"));
        Assert.That(await subscriptionService.ResumeSubscription("plan-owner", "40001"), Is.True);
        Assert.That(changingSubscription.EndsAt, Is.Null);
        Assert.That(slots > 0 ? await context.TierSlots.Select(s => s.Expires).FirstAsync()
            : (await context.OwnerShips.SingleAsync()).Expires, Is.EqualTo(expiry));
    }

    [TestCase(0)]
    [TestCase(4)]
    public async Task FullProrationRefundRestoresOnlyThatPeriodsTierAndDuplicatesDoNothing(int slots)
    {
        await SetupPlanSwitch(slots);
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var invoice = providerInvoices.Single();
        await subscriptionService.RefundPayment(RefundInvoice(invoice.Id, "updated", false));
        Assert.That((await context.SubscriptionPlanChanges.SingleAsync()).Refunded, Is.False);
        await subscriptionService.RefundPayment(RefundInvoice(invoice.Id, "updated"));
        await subscriptionService.RefundPayment(RefundInvoice(invoice.Id, "updated"));
        Assert.That((await context.SubscriptionPlanChanges.SingleAsync()).Refunded, Is.True);
        if (slots > 0)
            Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);
        else
            Assert.That((await context.OwnerShips.SingleAsync()).Product.Id, Is.EqualTo(originalPlan.Id));
        // Refunding a payment doesn't cancel the future plan or let a stale event restore refunded access.
        await subscriptionService.UpdateSubscription(new Webhook(new Meta(false, "subscription_updated", null), PlanState(201)));
        Assert.That(changingSubscription.Product.Id, Is.EqualTo(targetPlan.Id));
        Assert.That(await context.FiniteTransactions.CountAsync(), Is.EqualTo(2));
        var renewal = CreatePaymentWebhook("plan-owner", targetPlan.Id, "40001", DateTime.UtcNow.AddDays(28));
        renewal.Data.Attributes.BillingReason = "renewal";
        await subscriptionService.PaymentReceived(renewal);
        if (slots > 0)
            Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium_plus"), Is.True);
        else
            Assert.That((await context.OwnerShips.SingleAsync()).Product.Id, Is.EqualTo(targetPlan.Id));
    }

    [TestCase(0)]
    [TestCase(4)]
    public async Task RenewalRefundRemovesOnlyOnePeriodAndDoesNotCreditSpendableCoins(int slots)
    {
        await SetupPlanSwitch(slots);
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var before = slots > 0 ? await context.TierSlots.Select(s => s.Expires).ToArrayAsync()
            : await context.OwnerShips.Select(o => o.Expires).ToArrayAsync();
        var renewal = CreatePaymentWebhook("plan-owner", targetPlan.Id, "40001", DateTime.UtcNow.AddDays(28));
        renewal.Data.Attributes.BillingReason = "renewal";
        await subscriptionService.PaymentReceived(renewal);
        await subscriptionService.RefundPayment(RefundInvoice(renewal.Data.Id, "renewal", false));
        await subscriptionService.RefundPayment(RefundInvoice(renewal.Data.Id, "renewal"));
        await subscriptionService.RefundPayment(RefundInvoice(renewal.Data.Id, "renewal"));
        Assert.That(slots > 0 ? await context.TierSlots.Select(s => s.Expires).ToArrayAsync()
            : await context.OwnerShips.Select(o => o.Expires).ToArrayAsync(), Is.EqualTo(before));
        Assert.That(changingSubscription.User.Balance, Is.Zero);
        await subscriptionService.PaymentReceived(renewal);
        Assert.That(slots > 0 ? await context.TierSlots.Select(s => s.Expires).ToArrayAsync()
            : await context.OwnerShips.Select(o => o.Expires).ToArrayAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task RefundedInvoiceArrivingBeforePaymentCannotGrantAccessLater()
    {
        await SetupPlanSwitch();
        var renewal = CreatePaymentWebhook("plan-owner", originalPlan.Id, "40001", DateTime.UtcNow.AddDays(28));
        renewal.Data.Attributes.BillingReason = "renewal";
        var before = await context.TierSlots.Select(s => s.Expires).ToArrayAsync();
        await subscriptionService.RefundPayment(RefundInvoice(renewal.Data.Id, "renewal", createdAt: DateTime.UtcNow.AddDays(28)));
        await subscriptionService.PaymentReceived(renewal);
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(before));
    }

    [Test]
    public async Task ActiveSubscriptionWithPendingUpgradeInvoiceDoesNotGrantAccess()
    {
        await SetupPlanSwitch();
        planProvider.Setup(p => p.ChangeSubscription("40001", 202, true)).ReturnsAsync(() =>
        {
            AddProrationInvoice("pending");
            return providerSubscription = PlanState(202);
        });
        Assert.That((await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug)).Status, Is.EqualTo("pending"));
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);
        providerInvoices.Single().Status = "paid";
        Assert.That((await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug)).Status, Is.EqualTo("completed"));
        planProvider.Verify(p => p.ChangeSubscription("40001", 202, true), Times.Once);
    }

    [Test]
    public async Task AnInFlightChangeBlocksAnotherProviderMutation()
    {
        await SetupPlanSwitch();
        changingSubscription.ChangeLockUntil = DateTime.UtcNow.AddMinutes(2);
        await context.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug));
        planProvider.Verify(p => p.ChangeSubscription(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>()), Times.Never);
    }
    [Test]
    public async Task SelfServiceRefundValidatesOwnerAndAmountAndAppliesWithoutWaitingForWebhook()
    {
        await SetupPlanSwitch();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var invoice = providerInvoices.Single();
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.RefundSubscriptionPayment("someone-else", "40001", invoice.Id, null));
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.RefundSubscriptionPayment("plan-owner", "40001", invoice.Id, new() { Amount = 0 }));
        Assert.ThrowsAsync<ApiException>(() => subscriptionService.RefundSubscriptionPayment("plan-owner", "40001", invoice.Id, new() { Amount = 5001 }));
        planProvider.Verify(p => p.RefundInvoiceAsync(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        planProvider.Setup(p => p.RefundInvoiceAsync(invoice.Id, null)).ReturnsAsync(() =>
        {
            invoice.Refunded = true;
            invoice.RefundedAmount = invoice.Total;
            invoice.Status = "refunded";
            return new RefundResponse { Id = invoice.Id, Refunded = true, RefundedAmount = invoice.Total, Status = "refunded" };
        });
        await subscriptionService.RefundSubscriptionPayment("plan-owner", "40001", invoice.Id, null);
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium"), Is.True);
        await subscriptionService.RefundSubscriptionPayment("plan-owner", "40001", invoice.Id, null);
        planProvider.Verify(p => p.RefundInvoiceAsync(invoice.Id, null), Times.Once);
    }

    [Test]
    public async Task OldProrationRefundDoesNotUndoLaterPaidRenewal()
    {
        await SetupPlanSwitch();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var renewal = CreatePaymentWebhook("plan-owner", targetPlan.Id, "40001", DateTime.UtcNow.AddDays(28));
        renewal.Data.Attributes.BillingReason = "renewal";
        await subscriptionService.PaymentReceived(renewal);
        var before = await context.TierSlots.Select(s => s.Expires).ToArrayAsync();
        await subscriptionService.RefundPayment(RefundInvoice(providerInvoices.Single().Id, "updated"));
        Assert.That(await context.TierSlots.AllAsync(s => s.Tier == "premium_plus"), Is.True);
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(before));
    }

    [TestCase(0)]
    [TestCase(3)]
    public async Task LegacySingleSubscriptionSeparatesOnlyItsLastPaidContribution(int graceHours)
    {
        await SetupPlanSwitch(0);
        originalPlan.OwnershipSeconds += graceHours * 3600;
        context.OwnerShips.RemoveRange(context.OwnerShips);
        var purchase = await context.FiniteTransactions.SingleAsync(t => t.Amount < 0);
        purchase.SubscriptionId = null;
        purchase.Reference = "40001" + DateTime.UtcNow.ToString("yyyy-MM-dd");
        var paidEnd = DateTime.UtcNow.AddDays(40);
        foreach (var product in await context.Products.Where(p => p.Slug == "premium").ToListAsync())
            context.OwnerShips.Add(new OwnerShip { User = changingSubscription.User, Product = product, Expires = paidEnd });
        await context.SaveChangesAsync();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var independent = await context.OwnerShips.SingleAsync(o => o.SubscriptionId == null);
        Assert.That(independent.Expires, Is.EqualTo(paidEnd.AddDays(-28).AddHours(-graceHours)));
        var subscriptionAccess = await context.OwnerShips.SingleAsync(o => o.SubscriptionId == "40001");
        Assert.That(subscriptionAccess.Expires, Is.EqualTo(changingSubscription.RenewsAt.AddHours(graceHours)));
        Assert.That(subscriptionAccess.Product.Id, Is.EqualTo(targetPlan.Id));
        Assert.That(purchase.SubscriptionId, Is.EqualTo("40001"));
    }

    [Test]
    public async Task ReplayedPreUpgradeInvoiceCannotGrantAnotherPeriodAtNewTier()
    {
        await SetupPlanSwitch();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        var before = await context.TierSlots.Select(s => s.Expires).ToArrayAsync();
        Assert.ThrowsAsync<TransactionService.DupplicateTransactionException>(() =>
            subscriptionService.PaymentReceived(CreatePaymentWebhook("plan-owner", originalPlan.Id, "40001")));
        Assert.That(await context.TierSlots.Select(s => s.Expires).ToArrayAsync(), Is.EqualTo(before));
        Assert.That(changingSubscription.User.Balance, Is.Zero);
    }

    [Test]
    public async Task ProrationRefundIncludesExistingSinglePlanGracePeriod()
    {
        await SetupPlanSwitch(0);
        var access = await context.OwnerShips.SingleAsync();
        access.Expires = changingSubscription.RenewsAt.AddHours(3);
        await context.SaveChangesAsync();
        await subscriptionService.ChangePlan("plan-owner", "40001", targetPlan.Slug);
        await subscriptionService.RefundPayment(RefundInvoice(providerInvoices.Single().Id, "updated"));
        Assert.That(access.Product.Id, Is.EqualTo(originalPlan.Id));
        Assert.That(access.Expires, Is.EqualTo(changingSubscription.RenewsAt.AddHours(3)));
    }

}
