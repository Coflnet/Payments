using Coflnet.Payments.Models;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Controllers;

namespace Coflnet.Payments.Services;

public class ProductsServiceTests
{
    private SqliteConnection _connection;
    private DbContextOptions<PaymentContext> _contextOptions;
    private PaymentContext context;
    private ProductService service;
    private GroupService groupService;
    private PurchaseableProduct purchaseableProduct;
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
        service = new ProductService(NullLogger<ProductService>.Instance, context, groupService);

        purchaseableProduct = CreateProduct("test", Product.ProductType.SERVICE);

        context.SaveChanges();
    }

    private static PurchaseableProduct CreateProduct(string slug, Product.ProductType type)
    {
        return new PurchaseableProduct
        {
            Title = "Test",
            Slug = slug,
            Description = "Test",
            Cost = 1,
            OwnershipSeconds = 10,
            Type = type
        };
    }

    [Test]
    public async Task AddProduct()
    {
        var product = purchaseableProduct;
        await service.UpdateOrAddProduct(product);

        var loadedProduct = await service.GetProduct("test");
        Assert.AreEqual(product.Title, loadedProduct.Title);
        Assert.AreEqual(product.Slug, loadedProduct.Slug);
        Assert.AreEqual(product.Description, loadedProduct.Description);
        Assert.AreEqual(product.Cost, loadedProduct.Cost);
        Assert.AreEqual(product.OwnershipSeconds, loadedProduct.OwnershipSeconds);
        Assert.AreEqual(product.Type, loadedProduct.Type);
    }

    public async Task AddTopupProduct()
    {
        var product = new TopUpProduct
        {
            Title = "Test",
            Slug = "test",
            Description = "Test",
            Cost = 1,
            OwnershipSeconds = 1,
            Type = Product.ProductType.TOP_UP
        };
        await service.UpdateTopUpProduct(product);

        var loadedProduct = await service.GetTopupProduct("test");
        Assert.AreEqual(product.Title, loadedProduct.Title);
        Assert.AreEqual(product.Slug, loadedProduct.Slug);
        Assert.AreEqual(product.Description, loadedProduct.Description);
        Assert.AreEqual(product.Cost, loadedProduct.Cost);
        Assert.AreEqual(product.OwnershipSeconds, loadedProduct.OwnershipSeconds);
        Assert.AreEqual(product.Type, loadedProduct.Type);
    }

    [Test]
    public async Task ProductIsInGroup()
    {
        var product = purchaseableProduct;
        await service.UpdateOrAddProduct(product);

        await groupService.AddProductToGroup(product, "mygroup");
        var loadedGroup = await groupService.GetGroup("mygroup");
        Assert.AreEqual(1, loadedGroup.Products.Count);
        Assert.AreEqual(product.Slug, loadedGroup.Products.First().Slug);
    }
    [Test]
    public async Task UserOwnsProductInGroup()
    {
        var product = purchaseableProduct;

        var userService = new UserService(NullLogger<UserService>.Instance, context);
        var transactionService = new TransactionService(NullLogger<TransactionService>.Instance, context, userService, new NullTransationProducer(), null, null);
        var userController = new UserController(NullLogger<UserController>.Instance, context, transactionService, userService);
        
        await service.UpdateOrAddProduct(product);

        var otherProduct = new PurchaseableProduct
        {
            Title = "Different",
            Slug = "different",
            Description = "Test",
            Cost = 1,
            OwnershipSeconds = 100,
            Type = Product.ProductType.COLLECTABLE
        };

        await service.UpdateOrAddProduct(otherProduct);
        await groupService.AddProductToGroup(otherProduct, "mygroup");

        await groupService.AddProductToGroup(product, "mygroup");
        var loadedGroup = await groupService.GetGroup("mygroup");
        Assert.AreEqual(2, loadedGroup.Products.Count);

        var user = await userService.GetOrCreate("u1");
        user.Balance = 10000;
        await context.SaveChangesAsync();
        await userController.Purchase("u1", "different");
        var expires = await userController.GetLongest("u1", new(){"mygroup"});
        Assert.Greater(expires, DateTime.UtcNow.AddSeconds(10));
    }

    [Test]
    public async Task InGroupExtendsBaseService()
    {
        var userService = new UserService(NullLogger<UserService>.Instance, context);
        var groupService = new GroupService(NullLogger<GroupService>.Instance, context);
        var transactionService = new TransactionService(NullLogger<TransactionService>.Instance, context, userService, new NullTransationProducer(), null, groupService);

        purchaseableProduct.Type = Product.ProductType.SERVICE;
        var lowTierProduct = CreateProduct("b", Product.ProductType.SERVICE);
        await service.UpdateOrAddProduct(lowTierProduct);
        await groupService.AddProductToGroup(purchaseableProduct, purchaseableProduct.Slug);
        await groupService.AddProductToGroup(purchaseableProduct, "b");

        var user = await userService.GetOrCreate("u1");
        user.Balance = 10000;
        await context.SaveChangesAsync();

        var groups = await groupService.GetGroup("b");
        var testgroups = await groupService.GetGroup("test");

        await transactionService.PurchaseServie(purchaseableProduct.Slug, user.ExternalId, 1, "test");

        var allOwnerShips = context.Users.SelectMany(u => u.Owns);
        Assert.AreEqual(2, allOwnerShips.Count());
        var expiry = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        // both services expire in 10 seconds
        Assert.That(allOwnerShips.First(o=>o.Product == lowTierProduct).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));
        Assert.That(allOwnerShips.First(o=>o.Product == purchaseableProduct).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));


        await transactionService.PurchaseServie(lowTierProduct.Slug, user.ExternalId, 1, "test");
        allOwnerShips = context.Users.SelectMany(u => u.Owns);
        Assert.AreEqual(2, allOwnerShips.Count());
        // only one product has been extended
        Assert.That(allOwnerShips.First(o=>o.Product == lowTierProduct).Expires, Is.EqualTo(DateTime.UtcNow + TimeSpan.FromSeconds(20)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(allOwnerShips.First(o=>o.Product == purchaseableProduct).Expires, Is.EqualTo(expiry).Within(TimeSpan.FromSeconds(1)));

    }

    public class NullTransationProducer : ITransactionEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent)
        {
            return Task.CompletedTask;
        }
    }
}

