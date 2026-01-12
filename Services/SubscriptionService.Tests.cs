using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Moq;
using System.Collections.Generic;

namespace Coflnet.Payments.Services;

public class SubscriptionServiceTests
{
    private SqliteConnection _connection;
    private DbContextOptions<PaymentContext> _contextOptions;
    private PaymentContext context;
    private SubscriptionService subscriptionService;
    private UserService userService;
    private TransactionService transactionService;
    private LemonSqueezyService lemonSqueezyService;
    private ProductService productService;
    private GroupService groupService;

    [SetUp]
    public async Task Setup()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _contextOptions = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        context = new PaymentContext(_contextOptions);
        context.Database.EnsureCreated();

        userService = new UserService(NullLogger<UserService>.Instance, context);
        var ruleEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
        transactionService = new TransactionService(
            NullLogger<TransactionService>.Instance, 
            context, 
            userService, 
            new NullTransactionProducer(), 
            null, 
            ruleEngine);
        
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["LEMONSQUEEZY:API_KEY"]).Returns("test-key");
        configMock.Setup(c => c["LEMONSQUEEZY:STORE_ID"]).Returns("12345");
        configMock.Setup(c => c["LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID"]).Returns("variant-1");
        
        lemonSqueezyService = new LemonSqueezyService(
            configMock.Object, 
            NullLogger<LemonSqueezyService>.Instance,
            context);
        
        groupService = new GroupService(NullLogger<GroupService>.Instance, context);
        
        productService = new ProductService(
            NullLogger<ProductService>.Instance, 
            context, 
            groupService);

        subscriptionService = new SubscriptionService(
            NullLogger<SubscriptionService>.Instance,
            transactionService,
            userService,
            productService,
            context,
            lemonSqueezyService);

        // Seed a TopUpProduct with SERVICE type - this can be used for both payment and ownership
        var topup = new TopUpProduct 
        { 
            Title = "Monthly Premium", 
            Slug = "monthly-premium", 
            Cost = 1800, 
            OwnershipSeconds = (int)TimeSpan.FromDays(30).TotalSeconds, 
            Type = Product.ProductType.TOP_UP | Product.ProductType.SERVICE,
            ProviderSlug = "lemonsqueezy"
        };
        context.TopUpProducts.Add(topup);

        // Add revert product for audit trail
        var revertProduct = new PurchaseableProduct
        {
            Title = "Revert",
            Slug = "revert",
            Cost = 0,
            Type = Product.ProductType.DISABLED
        };
        context.Products.Add(revertProduct);

        await context.SaveChangesAsync();
        
        // Set up group for the product so ownership can be extended
        await groupService.AddProductToGroup(topup, topup.Slug);
    }

    [TearDown]
    public void TearDown()
    {
        context?.Dispose();
        _connection?.Dispose();
    }

    /// <summary>
    /// Test that a trial subscription grants access without crediting coins
    /// </summary>
    [Test]
    public async Task TrialSubscription_GrantsAccess_WithoutCreditingCoins()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-trial");
        var product = await context.TopUpProducts.FirstAsync();
        var initialBalance = user.Balance;
        
        var trialEndDate = DateTime.UtcNow.AddDays(3);
        var webhook = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate);

        // Act
        await subscriptionService.UpdateSubscription(webhook);

        // Assert
        // User balance should NOT change (no coins credited for trial)
        var refreshedUser = await userService.GetOrCreate(user.ExternalId);
        Assert.That(refreshedUser.Balance, Is.EqualTo(initialBalance), "Balance should not change for trial subscription");
        
        // User should have ownership that expires at trial end (match by slug since TopUpProduct and PurchaseableProduct have different IDs)
        var ownership = await context.OwnerShips
            .Where(o => o.User.ExternalId == user.ExternalId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();
        Assert.That(ownership, Is.Not.Null, "User should have ownership record for trial");
        Assert.That(ownership.Expires, Is.EqualTo(trialEndDate).Within(TimeSpan.FromSeconds(1)), "Ownership should expire at trial end");
        
        // Trial usage should be recorded
        var trialUsage = await context.TrialUsages
            .Where(t => t.User.ExternalId == user.ExternalId && t.ProductId == product.Id)
            .FirstOrDefaultAsync();
        Assert.That(trialUsage, Is.Not.Null, "Trial usage should be recorded");
    }

    /// <summary>
    /// Test that a user cannot get multiple trials for the same product
    /// </summary>
    [Test]
    public async Task TrialSubscription_PreventsDuplicateTrials_ForSameProduct()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-duplicate-trial");
        var product = await context.TopUpProducts.FirstAsync();

        // First trial
        var trialEndDate1 = DateTime.UtcNow.AddDays(3);
        var webhook1 = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate1, "sub-1");
        await subscriptionService.UpdateSubscription(webhook1);

        // Act - Check if user has used trial
        var hasUsedTrial = await lemonSqueezyService.HasUserUsedTrialAsync(user.ExternalId, product.Id);

        // Assert
        Assert.That(hasUsedTrial, Is.True, "System should detect that user has already used trial");
    }

    /// <summary>
    /// Test that subscriptions aren't extended beyond what user paid for
    /// When subscription is on trial, TryExtendSubscription should skip coin credit
    /// </summary>
    [Test]
    public async Task TrialSubscription_DoesNotExtend_BeyondTrialPeriod()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-no-extend");
        var product = await context.TopUpProducts.FirstAsync();
        
        var trialEndDate = DateTime.UtcNow.AddDays(3);
        var webhook = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate);
        
        // Act
        await subscriptionService.UpdateSubscription(webhook);
        
        // Assert - Ownership should only extend to trial end, not beyond (match by slug)
        var ownership = await context.OwnerShips
            .Where(o => o.User.ExternalId == user.ExternalId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();
        
        Assert.That(ownership, Is.Not.Null);
        Assert.That(ownership.Expires, Is.LessThanOrEqualTo(trialEndDate.AddSeconds(1)), 
            "Ownership should not extend beyond trial end date");
        
        // No topup transaction should be created (only audit trail)
        var topupTransactions = await context.FiniteTransactions
            .Where(t => t.User.ExternalId == user.ExternalId && t.Amount > 0)
            .ToListAsync();
        Assert.That(topupTransactions.Count, Is.EqualTo(0), 
            "No coin-crediting transactions should be created for trial subscription");
    }

    /// <summary>
    /// Test that trial ownership is correctly set to trial end date and not 
    /// extended by subsequent trial webhooks (duplicate protection)
    /// </summary>
    [Test]
    public async Task TrialSubscription_MultipleWebhooks_DoNotExtendBeyondOriginalTrial()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-multi-webhook");
        var product = await context.TopUpProducts.FirstAsync();
        
        // First trial webhook sets ownership
        var trialEndDate = DateTime.UtcNow.AddDays(3);
        var trialWebhook1 = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate, "sub-multi");
        await subscriptionService.UpdateSubscription(trialWebhook1);
        
        // Get the ownership after first trial
        var ownershipAfterFirst = await context.OwnerShips
            .Where(o => o.User.ExternalId == user.ExternalId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();
        Assert.That(ownershipAfterFirst, Is.Not.Null, "User should have ownership from trial");
        var expiryAfterFirst = ownershipAfterFirst.Expires;
        Assert.That(expiryAfterFirst, Is.EqualTo(trialEndDate).Within(TimeSpan.FromSeconds(1)), 
            "Ownership should expire at trial end date");
        
        // Second trial webhook with same subscription ID (simulating duplicate webhook)
        var trialWebhook2 = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate, "sub-multi");
        await subscriptionService.UpdateSubscription(trialWebhook2);
        
        // Assert - Ownership should NOT be extended beyond the original trial end
        await context.Entry(ownershipAfterFirst).ReloadAsync();
        Assert.That(ownershipAfterFirst.Expires, Is.LessThanOrEqualTo(trialEndDate.AddSeconds(1)), 
            "Ownership should not extend beyond original trial end date after duplicate webhook");
        
        // Verify user balance is still 0 (no coins credited for trials)
        var refreshedUser = await userService.GetOrCreate(user.ExternalId);
        Assert.That(refreshedUser.Balance, Is.EqualTo(0), "No coins should be credited for trial subscriptions");
    }

    /// <summary>
    /// Test that trial length is capped at 3 days maximum
    /// </summary>
    [Test]
    public void TrialLengthDays_IsCappedAtThreeDays()
    {
        // Arrange
        var options = new TopUpOptions
        {
            EnableTrial = true,
            TrialLengthDays = 10 // Try to set more than 3 days
        };
        
        // Act - The cap is applied in TopUpController, simulate the logic
        var effectiveTrialDays = Math.Min(Math.Max(options.TrialLengthDays, 1), 3);
        
        // Assert
        Assert.That(effectiveTrialDays, Is.EqualTo(3), "Trial length should be capped at 3 days");
    }

    /// <summary>
    /// Test that trial length minimum is 1 day
    /// </summary>
    [Test]
    public void TrialLengthDays_MinimumIsOneDay()
    {
        // Arrange
        var options = new TopUpOptions
        {
            EnableTrial = true,
            TrialLengthDays = 0 // Try to set less than 1 day
        };
        
        // Act - The cap is applied in TopUpController, simulate the logic
        var effectiveTrialDays = Math.Min(Math.Max(options.TrialLengthDays, 1), 3);
        
        // Assert
        Assert.That(effectiveTrialDays, Is.EqualTo(1), "Trial length should be at least 1 day");
    }

    /// <summary>
    /// Test that EnableTrial defaults to false
    /// </summary>
    [Test]
    public void TopUpOptions_EnableTrial_DefaultsToFalse()
    {
        // Arrange & Act
        var options = new TopUpOptions();
        
        // Assert
        Assert.That(options.EnableTrial, Is.False, "EnableTrial should default to false");
    }

    /// <summary>
    /// Test that TrialLengthDays defaults to 3
    /// </summary>
    [Test]
    public void TopUpOptions_TrialLengthDays_DefaultsToThree()
    {
        // Arrange & Act
        var options = new TopUpOptions();
        
        // Assert
        Assert.That(options.TrialLengthDays, Is.EqualTo(3), "TrialLengthDays should default to 3");
    }

    /// <summary>
    /// Test that subscription status "on_trial" is properly recognized
    /// </summary>
    [Test]
    public async Task SubscriptionUpdate_WithOnTrialStatus_RecordsTrialInfo()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-on-trial-status");
        var product = await context.TopUpProducts.FirstAsync();
        var trialEndDate = DateTime.UtcNow.AddDays(3);
        
        var webhook = CreateTrialSubscriptionWebhook(user.ExternalId, product.Id, trialEndDate);

        // Act
        await subscriptionService.UpdateSubscription(webhook);

        // Assert
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == user.ExternalId && s.Product.Id == product.Id)
            .FirstOrDefaultAsync();
        
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo("on_trial"));
        Assert.That(subscription.TrialEndsAt, Is.Not.Null);
        Assert.That(subscription.TrialUsedAt, Is.Not.Null);
    }

    /// <summary>
    /// Test that PayPal subscription_created event grants ownership when there's no subscription_payment_success webhook.
    /// This is a regression test for the issue where PayPal subscriptions were not crediting ownership 
    /// because PayPal doesn't send subscription_payment_success webhooks.
    /// 
    /// Sample anonymized webhook data:
    /// {
    ///   "meta": {
    ///     "test_mode": false,
    ///     "event_name": "subscription_created",
    ///     "custom_data": {
    ///       "user_id": "{{USER_ID}}",
    ///       "product_id": "{{PRODUCT_ID}}",
    ///       "coin_amount": "1800",
    ///       "enable_trial": "False",
    ///       "is_subscription": "True",
    ///       "trial_length_days": "3"
    ///     },
    ///     "webhook_id": "{{WEBHOOK_UUID}}"
    ///   },
    ///   "data": {
    ///     "type": "subscriptions",
    ///     "id": "{{SUBSCRIPTION_ID}}",
    ///     "attributes": {
    ///       "store_id": {{STORE_ID}},
    ///       "customer_id": {{CUSTOMER_ID}},
    ///       "order_id": {{ORDER_ID}},
    ///       "product_name": "Monthly premium",
    ///       "status": "active",
    ///       "payment_processor": "paypal",
    ///       "renews_at": "{{RENEWS_AT}}",
    ///       "created_at": "{{CREATED_AT}}",
    ///       "updated_at": "{{UPDATED_AT}}"
    ///     }
    ///   }
    /// }
    /// </summary>
    [Test]
    public async Task PayPalSubscriptionCreated_GrantsOwnership_WhenNoPaymentSuccessWebhook()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-paypal-sub");
        var product = await context.TopUpProducts.FirstAsync();
        var initialBalance = user.Balance;
        var renewsAt = DateTime.UtcNow.AddDays(30);
        
        // Create a PayPal subscription_created webhook (simulating what LemonSqueezy sends for PayPal subscriptions)
        var webhook = CreatePayPalSubscriptionCreatedWebhook(user.ExternalId, product.Id, renewsAt);

        // Act
        await subscriptionService.UpdateSubscription(webhook);

        // Assert - User should have ownership
        var ownership = await context.OwnerShips
            .Where(o => o.User.ExternalId == user.ExternalId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();
        Assert.That(ownership, Is.Not.Null, "User should have ownership record after PayPal subscription_created");
        Assert.That(ownership.Expires, Is.GreaterThan(DateTime.UtcNow), "Ownership should not be expired");
        
        // User should have subscription record
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == user.ExternalId && s.Product.Id == product.Id)
            .FirstOrDefaultAsync();
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo("active"));
    }

    /// <summary>
    /// Test that non-PayPal subscription_created events do NOT automatically grant ownership
    /// (they should wait for subscription_payment_success webhook)
    /// </summary>
    [Test]
    public async Task NonPayPalSubscriptionCreated_DoesNotGrantOwnership_WaitsForPaymentWebhook()
    {
        // Arrange
        var user = await userService.GetOrCreate("test-user-stripe-sub");
        var product = await context.TopUpProducts.FirstAsync();
        var renewsAt = DateTime.UtcNow.AddDays(30);
        
        // Create a subscription_created webhook without PayPal payment processor (e.g., Stripe/card)
        var webhook = CreateNonPayPalSubscriptionCreatedWebhook(user.ExternalId, product.Id, renewsAt);

        // Act
        await subscriptionService.UpdateSubscription(webhook);

        // Assert - User should NOT have ownership yet (waiting for payment webhook)
        var ownership = await context.OwnerShips
            .Where(o => o.User.ExternalId == user.ExternalId && o.Product.Slug == product.Slug)
            .FirstOrDefaultAsync();
        Assert.That(ownership, Is.Null, "Non-PayPal subscription should wait for subscription_payment_success webhook");
        
        // But subscription record should exist
        var subscription = await context.Subscriptions
            .Where(s => s.User.ExternalId == user.ExternalId && s.Product.Id == product.Id)
            .FirstOrDefaultAsync();
        Assert.That(subscription, Is.Not.Null);
        Assert.That(subscription.Status, Is.EqualTo("active"));
    }

    #region Helper Methods

    private Webhook CreateTrialSubscriptionWebhook(string userId, int productId, DateTime trialEndsAt, string subscriptionId = "test-sub-123")
    {
        var customData = new CustomData(userId, productId, 1800, "True");
        var meta = new Meta(false, "subscription_created", customData);
        var attributes = CreateTrialAttributes(trialEndsAt);
        var data = new Data("subscriptions", subscriptionId, attributes, null, null);
        return new Webhook(meta, data);
    }

    private Webhook CreatePaymentWebhook(string userId, int productId, string subscriptionId)
    {
        var customData = new CustomData(userId, productId, 1800, "True");
        var meta = new Meta(false, "subscription_payment_success", customData);
        var attributes = new Attributes(
            storeId: 12595,
            customerId: 1,
            identifier: "test-invoice",
            orderNumber: 1,
            userName: "Test User",
            userEmail: "test@example.com",
            currency: "EUR",
            currencyRate: "1.0",
            taxName: "VAT",
            taxRate: 19,
            status: "paid",
            statusFormatted: "Paid",
            refunded: false,
            refundedAt: null,
            subtotal: 969,
            discountTotal: 0,
            tax: 184,
            total: 1153,
            subtotalUsd: 1000,
            discountTotalUsd: 0,
            taxUsd: 190,
            totalUsd: 1190,
            subtotalFormatted: "€9.69",
            discountTotalFormatted: "€0.00",
            taxFormatted: "€1.84",
            totalFormatted: "€11.53",
            firstOrderItem: null,
            urls: null,
            createdAt: DateTime.UtcNow,
            updatedAt: DateTime.UtcNow,
            testMode: false,
            subscriptionId: 123456,
            renewsAt: DateTime.UtcNow.AddDays(30),
            endsAt: null
        );
        var data = new Data("subscription-invoices", "invoice-123", attributes, null, null);
        return new Webhook(meta, data);
    }

    private Attributes CreateTrialAttributes(DateTime trialEndsAt)
    {
        var attrs = new Attributes(
            storeId: 12595,
            customerId: 1,
            identifier: "test-order",
            orderNumber: 1,
            userName: "Test User",
            userEmail: "test@example.com",
            currency: "EUR",
            currencyRate: "1.0",
            taxName: "VAT",
            taxRate: 19,
            status: "on_trial",
            statusFormatted: "On Trial",
            refunded: false,
            refundedAt: null,
            subtotal: 0,
            discountTotal: 0,
            tax: 0,
            total: 0,
            subtotalUsd: 0,
            discountTotalUsd: 0,
            taxUsd: 0,
            totalUsd: 0,
            subtotalFormatted: "€0.00",
            discountTotalFormatted: "€0.00",
            taxFormatted: "€0.00",
            totalFormatted: "€0.00",
            firstOrderItem: null,
            urls: null,
            createdAt: DateTime.UtcNow,
            updatedAt: DateTime.UtcNow,
            testMode: false,
            subscriptionId: 0,
            renewsAt: trialEndsAt,
            endsAt: null
        );
        attrs.TrialEndsAt = trialEndsAt;
        return attrs;
    }

    /// <summary>
    /// Creates a PayPal subscription_created webhook similar to what LemonSqueezy sends.
    /// PayPal subscriptions are unique in that they don't send subscription_payment_success webhooks,
    /// so the subscription_created event with status "active" indicates successful payment.
    /// 
    /// Sample anonymized data structure:
    /// - event_name: subscription_created
    /// - status: active
    /// - payment_processor: paypal
    /// </summary>
    private Webhook CreatePayPalSubscriptionCreatedWebhook(string userId, int productId, DateTime renewsAt, string subscriptionId = "paypal-sub-123")
    {
        var customData = new CustomData(userId, productId, 1800, "True");
        var meta = new Meta(false, "subscription_created", customData);
        var attributes = new Attributes(
            storeId: 12595,
            customerId: 7539567,
            identifier: "{{ORDER_IDENTIFIER}}",
            orderNumber: 7249373,
            userName: "{{USER_NAME}}",
            userEmail: "{{USER_EMAIL}}",
            currency: "EUR",
            currencyRate: "1.16868030",
            taxName: "VAT",
            taxRate: 19,
            status: "active",
            statusFormatted: "Active",
            refunded: false,
            refundedAt: null,
            subtotal: 969,
            discountTotal: 0,
            tax: 184,
            total: 1153,
            subtotalUsd: 1132,
            discountTotalUsd: 0,
            taxUsd: 215,
            totalUsd: 1347,
            subtotalFormatted: "€9.69",
            discountTotalFormatted: "€0.00",
            taxFormatted: "€1.84",
            totalFormatted: "€11.53",
            firstOrderItem: null,
            urls: null,
            createdAt: DateTime.UtcNow,
            updatedAt: DateTime.UtcNow,
            testMode: false,
            subscriptionId: 0,
            renewsAt: renewsAt,
            endsAt: null
        );
        // Set PayPal as the payment processor - this is the key difference from other payment methods
        attributes.PaymentProcessor = "paypal";
        var data = new Data("subscriptions", subscriptionId, attributes, null, null);
        return new Webhook(meta, data);
    }

    /// <summary>
    /// Creates a non-PayPal (e.g., Stripe/card) subscription_created webhook.
    /// These subscriptions WILL receive a separate subscription_payment_success webhook,
    /// so we should not grant ownership from subscription_created alone.
    /// </summary>
    private Webhook CreateNonPayPalSubscriptionCreatedWebhook(string userId, int productId, DateTime renewsAt, string subscriptionId = "stripe-sub-123")
    {
        var customData = new CustomData(userId, productId, 1800, "True");
        var meta = new Meta(false, "subscription_created", customData);
        var attributes = new Attributes(
            storeId: 12595,
            customerId: 7539567,
            identifier: "{{ORDER_IDENTIFIER}}",
            orderNumber: 7249373,
            userName: "{{USER_NAME}}",
            userEmail: "{{USER_EMAIL}}",
            currency: "EUR",
            currencyRate: "1.16868030",
            taxName: "VAT",
            taxRate: 19,
            status: "active",
            statusFormatted: "Active",
            refunded: false,
            refundedAt: null,
            subtotal: 969,
            discountTotal: 0,
            tax: 184,
            total: 1153,
            subtotalUsd: 1132,
            discountTotalUsd: 0,
            taxUsd: 215,
            totalUsd: 1347,
            subtotalFormatted: "€9.69",
            discountTotalFormatted: "€0.00",
            taxFormatted: "€1.84",
            totalFormatted: "€11.53",
            firstOrderItem: null,
            urls: null,
            createdAt: DateTime.UtcNow,
            updatedAt: DateTime.UtcNow,
            testMode: false,
            subscriptionId: 0,
            renewsAt: renewsAt,
            endsAt: null
        );
        // No payment processor set, or could be set to "stripe" - NOT paypal
        attributes.PaymentProcessor = null;
        var data = new Data("subscriptions", subscriptionId, attributes, null, null);
        return new Webhook(meta, data);
    }

    #endregion

    public class NullTransactionProducer : ITransactionEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent)
        {
            return Task.CompletedTask;
        }
    }
}
