using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Payments.Controllers;

namespace Coflnet.Payments.Services
{
    public class ApplyTest
    {
        private SqliteConnection _connection;
        private DbContextOptions<PaymentContext> _contextOptions;
        private ApplyController applyController;
        private Product productB;
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
            var productService = new ProductService(NullLogger<ProductService>.Instance, context, groupService);

            applyController = new ApplyController(context, productService, groupService, rulesEngine);

            productB = new PurchaseableProduct() { Slug = "B", Cost = 600, OwnershipSeconds = 120 };
            var group = await groupService.GetOrAddGroup(productB.Slug);
            context.AddRange(productB);
            context.Rules.Add(new Rule() { Slug = "cheaperB", Targets = group, Priority = 1, Amount = 100, Flags = Rule.RuleFlags.LONGER });
            context.SaveChanges();
        }

        [Test]
        public async Task ApplyNewState()
        {
            await applyController.ApplyState(new SystemState()
            {
                TopUps = new List<TopUpProduct>()
                {
                    new TopUpProduct()
                    {
                        Cost = 100,
                    }
                },
                Products = new()
                {
                    new PurchaseableProduct()
                    {
                        Slug = "A",
                        Cost = 100,
                        OwnershipSeconds = 120,
                    }
                },
                Groups = new()
                {
                    {"XY", new string[]{"A"}}
                },
                Rules = new()
                {
                    new RuleCreate()
                    {
                        Slug = "betterRule",
                        TargetsGroup = "XY",
                        Priority = 1,
                        Amount = 100,
                        Flags = Rule.RuleFlags.LONGER
                    }
                }
            });

            Assert.IsTrue(context.TopUpProducts.Any());

            // assert that B is now disabled
            var product = await context.Products.FirstOrDefaultAsync(p => p.Slug == "B");
            Assert.IsTrue(product.Type.HasFlag(Product.ProductType.DISABLED));

            // assert group XY contains A
            var group = await context.Groups.Include(g=>g.Products).FirstOrDefaultAsync(g => g.Slug == "XY");
            Assert.IsTrue(group.Products.Any(p => p.Slug == "A"));

            var rules = await context.Rules.ToListAsync();
            Assert.IsFalse(rules.Where(r=>r.Slug == "cheaperB").Any());
            Assert.IsTrue(rules.Where(r => r.Slug == "betterRule").Any());
            Assert.AreEqual(1, rules.Count);

        }

    }
}