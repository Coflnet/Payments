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
