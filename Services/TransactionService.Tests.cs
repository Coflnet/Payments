using Coflnet.Payments.Models;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coflnet.Payments.Services;

public class TransactionServiceTests
{
    private SqliteConnection _connection;
    private DbContextOptions<PaymentContext> _contextOptions;
    private PaymentContext context;
    private TransactionService transactionService;
    private UserService userService;

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
    var ruleEngine = new Coflnet.Payments.Services.RuleEngine(NullLogger<Coflnet.Payments.Services.RuleEngine>.Instance, context);
    transactionService = new TransactionService(NullLogger<TransactionService>.Instance, context, userService, new NullTransationProducer(), null, ruleEngine);

        // seed a topup product and a purchaseable product
        var topup = new TopUpProduct { Title = "TopUp", Slug = "topup-test", Cost = 10, OwnershipSeconds = 0, Type = Product.ProductType.TOP_UP };
        context.TopUpProducts.Add(topup);

        var svc = new PurchaseableProduct { Title = "Service", Slug = "svc-test", Cost = 5, OwnershipSeconds = 60, Type = Product.ProductType.SERVICE };
        context.Products.Add(svc);

        await context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Close();
    }

    [Test]
    public async Task AmbientTransaction_IsReused_ByServiceMethods()
    {
        // create a user
        var user = await userService.GetOrCreate("u-test");
        user.Balance = 0;
        await context.SaveChangesAsync();

        // start an outer transaction and call AddTopUp and PurchaseService which should reuse the ambient transaction
        await using var tx = await transactionService.StartDbTransaction();
        try
        {
            await transactionService.AddTopUp(context.TopUpProducts.First().Id, user.ExternalId, "ref-ambient");
            // purchase a service using PurchaseService which uses AcquireTransactionIfNoneAsync internally
            await transactionService.PurchaseService("svc-test", user.ExternalId, 1, "ref-ambient-svc", context.Products.First());

            // commit outer transaction
            await tx.CommitAsync();
        }
        finally
        {
            await tx.DisposeAsync();
        }

        // verify transactions were created and user balance updated
        var ft = await context.FiniteTransactions.Where(t => t.Reference == "ref-ambient").FirstOrDefaultAsync();
    Assert.That(ft, Is.Not.Null, "TopUp transaction should be present after commit");
    var svcTrans = await context.FiniteTransactions.Where(t => t.Reference == "ref-ambient-svc").FirstOrDefaultAsync();
    Assert.That(svcTrans, Is.Not.Null, "Service purchase transaction should be present after commit");
    var refreshed = await userService.GetOrCreate(user.ExternalId);
    Assert.That(refreshed.Balance, Is.EqualTo(10 - 5));
    }

    [Test]
    public async Task OwnedTransaction_IsRolledBack_OnException_AndNoAmbientRemains()
    {
        // call AddTopUp with an invalid user id so CreateTransactionInTransaction will throw and the owned transaction should be rolled back and disposed
    var ex = NUnit.Framework.Assert.ThrowsAsync<ApiException>(async () => await transactionService.AddTopUp(context.TopUpProducts.First().Id, "nonexistent-user", "ref-bad"));

    // after exception there must be no current transaction left open
    Assert.That(context.Database.CurrentTransaction, Is.Null, "No ambient transaction should remain after rollback/dispose");
    }

    public class NullTransationProducer : ITransactionEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent)
        {
            return Task.CompletedTask;
        }
    }
}
