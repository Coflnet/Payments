using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Payments.Services
{
    public class RuleEngineTests
    {
        private SqliteConnection _connection;
        private DbContextOptions<PaymentContext> _contextOptions;
        private Group groupA;
        private Group groupB;
        private Product productB;
        private User user;
        private PaymentContext context;

        [SetUp]
        public async Task Setup()
        {
            // Create and open a connection. This creates the SQLite in-memory database, which will persist until the connection is closed
            // at the end of the test (see Dispose below).
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            // These options will be used by the context instances in this test suite, including the connection opened above.
            _contextOptions = new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(_connection)
                .EnableSensitiveDataLogging()
                .Options;

            // Create the schema and seed some data
            context = new PaymentContext(_contextOptions);

            context.Database.EnsureCreated();
            var groupService = new GroupService(NullLogger<GroupService>.Instance, context);
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);

            var productA = new PurchaseableProduct() { Slug = "A" };
            productB = new PurchaseableProduct() { Slug = "B", Cost = 600, OwnershipSeconds = 120 };
            context.AddRange(productA, productB);
            context.SaveChanges();
            groupA = await groupService.GetOrAddGroup(productA.Slug);
            groupB = await groupService.GetOrAddGroup(productB.Slug);
            user = new User() { ExternalId = "1", Balance = 0, Owns = new() };
            user.Owns.Add(new OwnerShip() { Product = productA, User = user });



            context.Users.Add(user);
            context.SaveChanges();
        }
        [TearDown]
        public void TearDown()
        {
            context.Dispose();
            _connection.Dispose();
        }

        [Test]
        public async Task AbsoluteDiscount()
        {
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
            var flags = Rule.RuleFlags.DISCOUNT;
            await AddRuleWithFlag(rulesEngine, flags);

            var result = await rulesEngine.GetAdjusted(productB, user);
            Assert.AreEqual(500, result.ModifiedProduct.Cost);
        }

        private async Task AddRuleWithFlag(RuleEngine rulesEngine, Rule.RuleFlags flags)
        {
            var cheaperRule = new RuleCreate() { Slug = "cheaperB", RequiresGroup = groupA.Slug, TargetsGroup = groupB.Slug, Priority = 1, Amount = 100, Flags = flags };

            await rulesEngine.AddOrUpdateRule(cheaperRule);
            await context.SaveChangesAsync();
        }

        [Test]
        public async Task ExtendAbsolute()
        {
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
            var flags = Rule.RuleFlags.LONGER;
            await AddRuleWithFlag(rulesEngine, flags);

            var result = await rulesEngine.GetAdjusted(productB, user);
            Assert.AreEqual(220, result.ModifiedProduct.OwnershipSeconds);
        }
        [Test]
        public async Task ExtendPercentage()
        {
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
            await AddRuleWithFlag(rulesEngine, Rule.RuleFlags.LONGER | Rule.RuleFlags.PERCENT);

            var result = await rulesEngine.GetAdjusted(productB, user);
            Assert.AreEqual(240, result.ModifiedProduct.OwnershipSeconds);
        }
        [Test]
        public async Task EarlyBreak()
        {
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
            var mainRule = new RuleCreate() { Slug = "cheapUnique", RequiresGroup = groupA.Slug, TargetsGroup = groupB.Slug, Priority = 2, Amount = 100, Flags = Rule.RuleFlags.LONGER | Rule.RuleFlags.EARLY_BREAK };
            var cheaperRule = new RuleCreate() { Slug = "cheaperB", RequiresGroup = groupA.Slug, TargetsGroup = groupB.Slug, Priority = 1, Amount = 1000, Flags = Rule.RuleFlags.LONGER | Rule.RuleFlags.PERCENT };

            await rulesEngine.AddOrUpdateRule(mainRule);
            await rulesEngine.AddOrUpdateRule(cheaperRule);
            await context.SaveChangesAsync();

            var result = await rulesEngine.GetAdjusted(productB, user);
            Assert.AreEqual(220, result.ModifiedProduct.OwnershipSeconds);
        }
        [Test]
        public async Task RequiresNone()
        {
            var rulesEngine = new RuleEngine(NullLogger<RuleEngine>.Instance, context);
            var cheaperRule = new RuleCreate() { Slug = "cheaperB", TargetsGroup = groupB.Slug, Priority = 1, Amount = 100, Flags = Rule.RuleFlags.LONGER };

            await rulesEngine.AddOrUpdateRule(cheaperRule);
            await context.SaveChangesAsync();

            var result = await rulesEngine.GetAdjusted(productB, user);
            Assert.AreEqual(220, result.ModifiedProduct.OwnershipSeconds);
        }

        PaymentContext CreateContext() => new PaymentContext(_contextOptions);

        public void Dispose() => _connection.Dispose();
    }
}