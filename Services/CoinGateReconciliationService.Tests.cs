using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.CoinGate;
using Coflnet.Payments.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Payments.Services;

public class CoinGateReconciliationServiceTests
{
    private SqliteConnection connection;
    private PaymentContext context;
    private FakeCoinGateService coinGate;
    private CoinGateReconciliationService service;
    private CoinGateOrderProcessor processor;

    [SetUp]
    public async Task SetUp()
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        context = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new User { ExternalId = "207906", Locale = "en-US", Country = "DE" });
        context.TopUpProducts.Add(new TopUpProduct
        {
            Id = 153,
            Title = "1,800 CoflCoins",
            Slug = "c_cc_1800",
            Cost = 1800,
            Price = 9.69m,
            CurrencyCode = "EUR",
            Type = Product.ProductType.TOP_UP
        });
        await context.SaveChangesAsync();

        var events = new NullEventProducer();
        var userService = new UserService(NullLogger<UserService>.Instance, context);
        var transactionService = new TransactionService(
            NullLogger<TransactionService>.Instance, context, userService, events, null,
            new RuleEngine(NullLogger<RuleEngine>.Instance, context));
        processor = new CoinGateOrderProcessor(context, transactionService, events, NullLogger<CoinGateOrderProcessor>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["COINGATE:API_TOKEN"] = "x",
            ["COINGATE:RECEIVE_CURRENCY"] = "EUR"
        }).Build();
        coinGate = new FakeCoinGateService(config);
        service = new CoinGateReconciliationService(null, coinGate, config, NullLogger<CoinGateReconciliationService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        context.Dispose();
        connection.Dispose();
    }

    private static CoinGateOrderResponse PaidOrder(long id = 37656726, string orderId = "CG-207906-153-639205761863254622", decimal price = 9.69m) => new()
    {
        Id = id,
        OrderId = orderId,
        Status = CoinGateOrderStatus.Paid,
        PriceAmount = price,
        PriceCurrency = "EUR",
        ReceiveAmount = 9.59m,
        PayCurrency = "BNB",
        CreatedAt = new DateTime(2026, 7, 25, 11, 36, 26, DateTimeKind.Utc),
        PaidAt = new DateTime(2026, 7, 25, 11, 40, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task MissingPaidOrder_IsCreditedOnceWithPaymentRecord()
    {
        coinGate.Orders.Add(PaidOrder());
        var before = CoinGateReconciliationService.Reconciled.Value;

        var recorded = await service.ReconcileAsync(context, processor);

        Assert.That(recorded, Is.EqualTo(1));
        Assert.That(CoinGateReconciliationService.Reconciled.Value, Is.EqualTo(before + 1));
        Assert.That((await context.Users.SingleAsync()).Balance, Is.EqualTo(1800));
        var record = await context.PaymentRecords.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(record.Country, Is.EqualTo("DE"));
            Assert.That(record.Provider, Is.EqualTo("coingate"));
            Assert.That(record.GrossAmount, Is.EqualTo(9.69m));
            Assert.That(record.NetAmount, Is.EqualTo(9.59m));
            Assert.That(record.ProcessorFee, Is.EqualTo(0.10m));
            Assert.That(record.ExternalTransactionId, Is.EqualTo("37656726"));
            Assert.That(record.CoinAmount, Is.EqualTo(1800));
        });
    }

    [Test]
    public async Task AlreadyRecordedOrder_IsSkipped()
    {
        coinGate.Orders.Add(PaidOrder());
        await processor.ProcessPaidOrderAsync(CoinGateOrder.FromResponse(PaidOrder()), "207906", "153", 1800, "callback");

        var recorded = await service.ReconcileAsync(context, processor);

        Assert.That(recorded, Is.EqualTo(0));
        Assert.That((await context.Users.SingleAsync()).Balance, Is.EqualTo(1800));
        Assert.That(await context.FiniteTransactions.CountAsync(t => t.Reference == "coingate:37656726"), Is.EqualTo(1));
        Assert.That(await context.PaymentRecords.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task SecondRun_DoesNotCreditAgain()
    {
        coinGate.Orders.Add(PaidOrder());

        await service.ReconcileAsync(context, processor);
        var second = await service.ReconcileAsync(context, processor);

        Assert.That(second, Is.EqualTo(0));
        Assert.That((await context.Users.SingleAsync()).Balance, Is.EqualTo(1800));
        Assert.That(await context.PaymentRecords.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PriceMismatch_IsSkippedAndCountedUnreconcilable()
    {
        coinGate.Orders.Add(PaidOrder(price: 7.75m));
        var before = CoinGateReconciliationService.Unreconcilable.Value;

        var recorded = await service.ReconcileAsync(context, processor);

        Assert.That(recorded, Is.EqualTo(0));
        Assert.That(CoinGateReconciliationService.Unreconcilable.Value, Is.EqualTo(before + 1));
        Assert.That((await context.Users.SingleAsync()).Balance, Is.EqualTo(0));
        Assert.That(await context.PaymentRecords.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task ForeignOrderIdFormat_IsSkipped()
    {
        coinGate.Orders.Add(PaidOrder(orderId: "SHOP-12345"));

        var recorded = await service.ReconcileAsync(context, processor);

        Assert.That(recorded, Is.EqualTo(0));
        Assert.That(await context.FiniteTransactions.AnyAsync(), Is.False);
    }

    [Test]
    public async Task FailingOrder_DoesNotStopTheLoop()
    {
        coinGate.Orders.Add(PaidOrder(id: 1, orderId: "CG-nobody-153-1"));
        coinGate.Orders.Add(PaidOrder(id: 2));

        var recorded = await service.ReconcileAsync(context, processor);

        Assert.That(recorded, Is.EqualTo(1));
        Assert.That(await context.FiniteTransactions.AnyAsync(t => t.Reference == "coingate:2"), Is.True);
    }

    private sealed class FakeCoinGateService : CoinGateService
    {
        public List<CoinGateOrderResponse> Orders { get; } = new();

        public FakeCoinGateService(IConfiguration config)
            : base(config, NullLogger<CoinGateService>.Instance)
        {
        }

        public override Task<List<CoinGateOrderResponse>> ListPaidOrdersAsync(DateTime from)
            => Task.FromResult(Orders.ToList());
    }

    private sealed class NullEventProducer : ITransactionEventProducer, IPaymentEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent) => Task.CompletedTask;
        public Task ProduceEvent(PaymentEvent paymentEvent) => Task.CompletedTask;
    }
}
