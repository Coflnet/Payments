using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.CoinGate;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Payments.Controllers;

public class CallbackControllerTests
{
    [Test]
    public void LemonSqueezy_EmptySecret_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CreateLemonSqueezyController("", Array.Empty<byte>()));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-hex")]
    [TestCase("00")]
    public async Task LemonSqueezy_InvalidSignature_IsRejected(string signature)
    {
        var controller = CreateLemonSqueezyController(
            "test-secret",
            Encoding.UTF8.GetBytes("not valid JSON"));

        var result = await controller.LemonSqueezy(signature);

        Assert.That(result, Is.TypeOf<UnauthorizedResult>());
    }

    [Test]
    public async Task LemonSqueezy_ValidSignature_IsAccepted()
    {
        const string secret = "test-secret";
        var payload = Encoding.UTF8.GetBytes(
            """
            {
              "meta": {
                "event_name": "subscription_payment_failed",
                "custom_data": {
                  "user_id": "test-user",
                  "product_id": 136,
                  "coin_amount": 2100,
                  "is_subscription": "true"
                }
              },
              "data": {
                "type": "subscription-invoices",
                "id": "1",
                "attributes": {}
              }
            }
            """);
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
        var controller = CreateLemonSqueezyController(secret, payload);

        var result = await controller.LemonSqueezy(signature);

        Assert.That(result, Is.TypeOf<OkResult>());
    }

    [Test]
    public async Task CoinGate_PaidCallbackWithoutStoredCountry_CreditsUser()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new PaymentContext(options);
        await context.Database.EnsureCreatedAsync();

        context.Users.Add(new User { ExternalId = "207906", Locale = "en-US" });
        context.TopUpProducts.Add(new TopUpProduct
        {
            Id = 153,
            Title = "1,800 CoflCoins",
            Slug = "c_cc_1800",
            Cost = 1800,
            Type = Product.ProductType.TOP_UP
        });
        await context.SaveChangesAsync();

        var events = new NullEventProducer();
        var userService = new UserService(NullLogger<UserService>.Instance, context);
        var ruleEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
        var transactionService = new TransactionService(
            NullLogger<TransactionService>.Instance,
            context,
            userService,
            events,
            null,
            ruleEngine);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("LEMONSQUEEZY:SECRET", "test")
            })
            .Build();
        var controller = new CallbackController(
            config,
            NullLogger<CallbackController>.Instance,
            context,
            transactionService,
            null,
            events,
            null,
            NullLogger<GooglePayController>.Instance,
            null,
            null,
            null,
            new VerifiedCoinGateService(config));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.ContentType = "application/json";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "id": 37656726,
              "order_id": "CG-207906-153-639205761863254622",
              "status": "paid",
              "price_amount": "9.69",
              "price_currency": "EUR",
              "receive_amount": "9.59",
              "pay_currency": "BNB",
              "created_at": "2026-07-25T11:36:26+00:00"
            }
            """));

        var result = await controller.CoinGate("207906", 153, 1800);

        Assert.That(result, Is.TypeOf<OkResult>());
        Assert.That((await context.Users.SingleAsync()).Balance, Is.EqualTo(1800));
        Assert.That(
            await context.FiniteTransactions.AnyAsync(t => t.Reference == "coingate:37656726"),
            Is.True);
    }

    [Test]
    public async Task LemonSqueezyOrder_NullUserCountry_StoresCustomerCountryOnUserAndRecord()
    {
        var lookup = new FakeLemonSqueezyLookup { Location = new("CA", "Ontario") };
        var (user, context) = await PostLemonSqueezyOrder(lookup, "ls-ca-buyer");

        var record = await context.PaymentRecords.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(lookup.CustomerIds, Is.EqualTo(new[] { "4242" }));
            Assert.That(user.Country, Is.EqualTo("CA"));
            Assert.That(record.Country, Is.EqualTo("CA"));
            Assert.That(record.State, Is.EqualTo("Ontario"));
        });
    }

    [Test]
    public async Task LemonSqueezyOrder_ExistingUserCountry_IsNotOverwritten()
    {
        var lookup = new FakeLemonSqueezyLookup { Location = new("CA", null) };
        var (user, context) = await PostLemonSqueezyOrder(lookup, "ls-de-buyer", "DE");

        Assert.Multiple(() =>
        {
            Assert.That(user.Country, Is.EqualTo("DE"));
            Assert.That(context.PaymentRecords.Single().Country, Is.EqualTo("CA"));
        });
    }

    [Test]
    public async Task LemonSqueezyOrder_FailedLookup_StillCompletesTopUp()
    {
        var lookup = new FakeLemonSqueezyLookup { Throw = true };
        var (user, context) = await PostLemonSqueezyOrder(lookup, "ls-fail-buyer");

        Assert.Multiple(() =>
        {
            Assert.That(user.Country, Is.Null);
            Assert.That(user.Balance, Is.EqualTo(1800));
            Assert.That(context.PaymentRecords.Single().Country, Is.Null);
        });
    }

    /// <summary>
    /// Reported exploit class: custom data can be attached to any Lemon Squeezy checkout through URL
    /// parameters, so an unsigned coin amount, product or user in the webhook must never be credited.
    /// </summary>
    [Test]
    public async Task LemonSqueezyOrder_UnsignedCustomData_IsRejected()
    {
        var (user, context) = await PostLemonSqueezyOrder(new FakeLemonSqueezyLookup(), "ls-forger", sign: false, expectRejected: true);
        Assert.That(user.Balance, Is.Zero);
        Assert.That(await context.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await context.PaymentRecords.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task LemonSqueezyOrder_TamperedCoinAmount_IsRejected()
    {
        var (user, _) = await PostLemonSqueezyOrder(new FakeLemonSqueezyLookup(), "ls-forger", claimedCoins: 2160000, expectRejected: true);
        Assert.That(user.Balance, Is.Zero);
    }

    [Test]
    public async Task LemonSqueezyOrder_PaidLessThanSignedPrice_IsRejected()
    {
        var (user, _) = await PostLemonSqueezyOrder(new FakeLemonSqueezyLookup(), "ls-forger", subtotal: 1, expectRejected: true);
        Assert.That(user.Balance, Is.Zero);
    }

    [Test]
    public async Task LemonSqueezyOrder_OtherVariant_IsRejected()
    {
        var (user, _) = await PostLemonSqueezyOrder(new FakeLemonSqueezyLookup(), "ls-forger", orderVariant: 555, expectRejected: true);
        Assert.That(user.Balance, Is.Zero);
    }

    [Test]
    public async Task LemonSqueezyOrder_SignedCustomAmountOnItsVariant_IsCredited()
    {
        var (user, _) = await PostLemonSqueezyOrder(new FakeLemonSqueezyLookup(), "ls-buyer",
            signedCoins: 21600, claimedCoins: 21600, priceCents: 11628, subtotal: 11628, orderVariant: 777);
        Assert.That(user.Balance, Is.EqualTo(21600));
    }

    private static async Task<(User user, PaymentContext context)> PostLemonSqueezyOrder(
        FakeLemonSqueezyLookup lookup, string externalId, string userCountry = null, bool sign = true,
        long signedCoins = 1800, long claimedCoins = 1800, long priceCents = 999, int subtotal = 999,
        long orderVariant = 0, bool expectRejected = false)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var context = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var user = new User { ExternalId = externalId, Locale = "en-US", Country = userCountry };
        context.Users.Add(user);
        context.TopUpProducts.Add(new TopUpProduct
        {
            Id = 153,
            Title = "1,800 CoflCoins",
            Slug = "c_cc_1800",
            Cost = 1800,
            Type = Product.ProductType.TOP_UP
        });
        await context.SaveChangesAsync();
        var events = new NullEventProducer();
        var transactionService = new TransactionService(
            NullLogger<TransactionService>.Instance, context,
            new UserService(NullLogger<UserService>.Instance, context),
            events, null, new RuleEngine(NullLogger<RuleEngine>.Instance, context));
        const string secret = "test-secret";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string>("LEMONSQUEEZY:SECRET", secret) })
            .Build();
        var controller = new CallbackController(
            config, NullLogger<CallbackController>.Instance, context, transactionService,
            null, events, null, NullLogger<GooglePayController>.Instance, null, null, null, null,
            lookup);
        var customSignature = sign
            ? $"\"signature\": \"{LemonSqueezyCustomDataSigner.Sign(secret, externalId, 153, signedCoins, "False", null, 777, priceCents)}\","
            : "";
        var firstOrderItem = orderVariant > 0
            ? $$"""
              "first_order_item": { "id": 1, "order_id": 9001, "product_id": 1, "variant_id": {{orderVariant}}, "price_id": 1,
                "product_name": "Coins", "variant_name": "Coins", "price": {{subtotal}},
                "created_at": "2026-09-01T10:00:00Z", "updated_at": "2026-09-01T10:00:00Z", "test_mode": false },
              """
            : "";
        var payload = Encoding.UTF8.GetBytes($$"""
            {
              "meta": {
                "event_name": "order_created",
                "custom_data": { "user_id": "{{externalId}}", "product_id": "153", "coin_amount": "{{claimedCoins}}", "is_subscription": "False",
                                 "variant_id": "777", "price_cents": "{{priceCents}}", {{customSignature}} "enable_trial": "False" }
              },
              "data": {
                "type": "orders",
                "id": "9001",
                "attributes": {
                  "store_id": 1, "customer_id": 4242, "identifier": "ls-order-{{externalId}}",
                  "status": "paid", "currency": "USD", "total": {{subtotal}}, "subtotal": {{subtotal}}, "tax": 0,
                  {{firstOrderItem}}
                  "user_name": "Buyer", "user_email": "buyer@example.com",
                  "created_at": "2026-09-01T10:00:00Z"
                }
              }
            }
            """);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Body = new MemoryStream(payload);
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
        var result = await controller.LemonSqueezy(signature);
        Assert.That(result, expectRejected ? Is.TypeOf<BadRequestResult>() : Is.TypeOf<OkResult>());
        await context.Entry(user).ReloadAsync();
        return (user, context);
    }

    private sealed class FakeLemonSqueezyLookup : ILemonSqueezyCustomerLookup
    {
        public LemonSqueezyCustomerLocation Location { get; set; }
        public bool Throw { get; set; }
        public List<string> CustomerIds { get; } = new();

        public Task<LemonSqueezyCustomerLocation> GetCustomerLocationAsync(string customerId)
        {
            CustomerIds.Add(customerId);
            if (Throw)
                throw new InvalidOperationException("LS down");
            return Task.FromResult(Location);
        }

        public Task<LemonSqueezyCustomerLocation> GetOrderCustomerLocationAsync(string orderId)
            => throw new NotSupportedException();
    }

    private static CallbackController CreateLemonSqueezyController(string secret, byte[] payload)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("LEMONSQUEEZY:SECRET", secret)
            })
            .Build();
        var controller = new CallbackController(
            config,
            NullLogger<CallbackController>.Instance,
            null,
            null,
            null,
            null,
            null,
            NullLogger<GooglePayController>.Instance,
            null,
            null,
            null,
            null);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Body = new MemoryStream(payload);
        return controller;
    }

    private sealed class VerifiedCoinGateService : CoinGateService
    {
        public VerifiedCoinGateService(IConfiguration config)
            : base(config, NullLogger<CoinGateService>.Instance)
        {
        }

        public override Task<bool> VerifyCallback(
            CoinGateCallback callback,
            string expectedUserId,
            int expectedProductId,
            decimal expectedCoinAmount)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class NullEventProducer : ITransactionEventProducer, IPaymentEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent) => Task.CompletedTask;

        public Task ProduceEvent(PaymentEvent paymentEvent) => Task.CompletedTask;
    }
}
