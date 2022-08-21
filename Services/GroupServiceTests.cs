using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class GroupServiceTests
{
    private SqliteConnection _connection;
    private DbContextOptions<PaymentContext> _contextOptions;
    private Models.Group groupA;
    private Models.Group groupB;
    private PurchaseableProduct productB;
    private PurchaseableProduct productA;
    private PaymentContext context;
    private GroupService groupService;

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
        groupService = new GroupService(NullLogger<GroupService>.Instance, context);

        productA = new PurchaseableProduct() { Slug = "A" };
        productB = new PurchaseableProduct() { Slug = "B", Cost = 600, OwnershipSeconds = 120 };
        context.AddRange(productA, productB);
        context.SaveChanges();
        groupA = await groupService.GetOrAddGroup(productA.Slug);
        groupB = await groupService.GetOrAddGroup(productB.Slug);
        await groupService.AddProductToGroup(productA, groupB.Slug);

        context.SaveChanges();
    }
    [Test]
    public async Task ExtendGroups()
    {
        var products = await groupService.GetProductGroupsForProduct(productA);
        
    }
}

