using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Payments.Controllers;
using PayPalCheckoutSdk.Core;

namespace Coflnet.Payments.Services;

/// <summary>
/// In-memory database with the subscription plans as provisioned today (Lemon Squeezy rows reused by every provider).
/// </summary>
public abstract class DirectSubscriptionTestBase
{
    protected SqliteConnection connection;
    protected PaymentContext db;
    protected UserService users;
    protected TransactionService transactions;
    protected ProductService products;
    protected GroupService groups;
    protected TopUpProduct premium;
    protected TopUpProduct premiumPlus;
    protected List<PaymentEvent> publishedPayments;
    /// <summary>The next publishes that fail, as Kafka can</summary>
    protected int failingPublishes;
    protected const int FourWeeks = 2430000;

    /// <summary>VAT rates (EXPERT_CONFIG:VAT_RATE_BASIS_POINTS) as appsettings.json ships them, for a few direct countries</summary>
    protected static readonly Dictionary<string, string> VatRates = new()
    {
        [SubscriptionTax.RateKeyPrefix + "DE"] = "1900", [SubscriptionTax.RateKeyPrefix + "AT"] = "2000",
        [SubscriptionTax.RateKeyPrefix + "FI"] = "2550", [SubscriptionTax.RateKeyPrefix + "GB"] = "2000",
        [SubscriptionTax.RateKeyPrefix + "US"] = "0"
    };

    protected static SubscriptionTax Tax() => new(new ConfigurationBuilder().AddInMemoryCollection(VatRates).Build());

    [SetUp]
    public async Task SetUpDatabase()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        users = new UserService(NullLogger<UserService>.Instance, db);
        transactions = new TransactionService(NullLogger<TransactionService>.Instance, db, users,
            new SubscriptionServiceTests.NullTransactionProducer(), null, new RuleEngine(NullLogger<RuleEngine>.Instance, db));
        groups = new GroupService(NullLogger<GroupService>.Instance, db);
        products = new ProductService(NullLogger<ProductService>.Instance, db, groups);
        premium = Plan("l_premium", "4 weeks premium", 9.69m, 1800);
        premiumPlus = Plan("l_prem_plus", "4 weeks prem+", 35.69m, 8500);
        db.TopUpProducts.AddRange(premium, premiumPlus);
        db.Products.Add(new PurchaseableProduct { Slug = "revert", Title = "Revert", Type = Product.ProductType.SERVICE });
        foreach (var tier in new[] { "premium", "premium_plus" })
            db.Products.Add(new PurchaseableProduct { Slug = tier, Title = tier, Type = Product.ProductType.SERVICE });
        await db.SaveChangesAsync();
        await groups.AddProductToGroup(premium, premium.Slug);
        await groups.AddProductToGroup(premium, "premium");
        await groups.AddProductToGroup(premiumPlus, premiumPlus.Slug);
        await groups.AddProductToGroup(premiumPlus, "premium_plus");
        await groups.AddProductToGroup(premiumPlus, "premium");
        publishedPayments = [];
    }

    [TearDown]
    public async Task TearDownDatabase()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static TopUpProduct Plan(string slug, string title, decimal price, decimal cost) => new()
    {
        Slug = slug, Title = title, Price = price, Cost = cost, CurrencyCode = "eur", ProviderSlug = "lemonsqueezy",
        OwnershipSeconds = FourWeeks, Type = Product.ProductType.SERVICE
    };

    protected SubscriptionService Service(IStripeSubscriptionGateway stripe = null, IPayPalSubscriptionApi paypal = null, PayPalPlanCatalog paypalPlans = null) =>
        new(NullLogger<SubscriptionService>.Instance, transactions, users, products, db,
            new LemonSqueezyService(new ConfigurationBuilder().Build(), NullLogger<LemonSqueezyService>.Instance, db,
                new VariantCacheService(NullLogger<VariantCacheService>.Instance)),
            stripe, paypal, paypalPlans, Tax());

    protected CallbackController Callback(SubscriptionService service, IDictionary<string, string> settings = null,
        PayPalHttpClient paypalClient = null)
    {
        var values = new Dictionary<string, string> { ["LEMONSQUEEZY:SECRET"] = "test" };
        foreach (var setting in settings ?? new Dictionary<string, string>())
            values[setting.Key] = setting.Value;
        var producer = new Mock<IPaymentEventProducer>();
        producer.Setup(p => p.ProduceEvent(It.IsAny<PaymentEvent>())).Returns<PaymentEvent>(payment =>
        {
            if (failingPublishes-- > 0)
                throw new InvalidOperationException("Simulated Kafka failure");
            publishedPayments.Add(payment);
            return Task.CompletedTask;
        });
        var controller = new CallbackController(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<CallbackController>.Instance, db, transactions, paypalClient, producer.Object, service,
            NullLogger<GooglePayController>.Instance, null, products,
            new CreatorCodeService(NullLogger<CreatorCodeService>.Instance, db), null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    /// <summary>Paid periods granted for a reference (the purchase leg, without the coin credit)</summary>
    protected Task<int> Grants(string reference) =>
        db.FiniteTransactions.CountAsync(t => t.Reference == reference);

    protected Task<OwnerShip> Access(string subscriptionId) =>
        db.OwnerShips.Include(o => o.Product).SingleOrDefaultAsync(o => o.SubscriptionId == subscriptionId);

    protected Task<UserSubscription> Subscription(string externalId) =>
        db.Subscriptions.Include(s => s.Product).Include(s => s.User).SingleOrDefaultAsync(s => s.ExternalId == externalId);

    protected async Task<PaymentRequest> SeedCheckout(string provider, string sessionId, TopUpProduct plan, decimal amount, string userId = "buyer")
    {
        var request = new PaymentRequest
        {
            User = await users.GetOrCreate(userId), ProductId = plan, Provider = provider, SessionId = sessionId,
            Amount = amount, CoinAmount = plan.Cost, State = PaymentRequest.Status.CREATED, CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.PaymentRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    protected async Task<User> Buyer(string userId = "buyer") =>
        await db.Users.SingleAsync(u => u.ExternalId == userId);

    protected async Task<PaymentRequest.Status?> RefundMark(string provider, string paymentId) =>
        (await db.PaymentRequests.SingleOrDefaultAsync(r => r.Provider == provider && r.SessionId == paymentId))?.State;

    protected Task<int> PaymentRecords(string provider) =>
        db.PaymentRecords.CountAsync(r => r.Provider == provider && r.IsSubscriptionPayment);
}
