using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using ProductPurchase = Google.Apis.AndroidPublisher.v3.Data.ProductPurchase;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Payments.Controllers;

public class GooglePayControllerTests
{
    [TestCase("buyer", true)]
    [TestCase("other-buyer", false)]
    public async Task AcknowledgedPurchaseRequiresMatchingCredit(string requestUser, bool valid)
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { ExternalId = "buyer", Balance = 100 };
        var product = new TopUpProduct { Slug = "coins", ProviderSlug = "googlepay", Type = Product.ProductType.TOP_UP, Cost = 100 };
        db.AddRange(user, product, new FiniteTransaction { User = user, Product = product, Reference = "ORDER", Amount = 100 });
        await db.SaveChangesAsync();
        var publisher = new Mock<IGooglePlayPublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.VerifyProductPurchaseAsync("package", "coins", "token"))
            .ReturnsAsync(new ProductPurchase { PurchaseState = 0, AcknowledgementState = 1, OrderId = "ORDER" });
        var payments = new Mock<IPaymentEventProducer>(MockBehavior.Strict);
        var transactions = new TransactionService(NullLogger<TransactionService>.Instance, db,
            new UserService(NullLogger<UserService>.Instance, db), Mock.Of<ITransactionEventProducer>(), null,
            new RuleEngine(NullLogger<RuleEngine>.Instance, db));
        var controller = new GooglePayController(NullLogger<GooglePayController>.Instance, publisher.Object,
            transactions, payments.Object, new ProductService(NullLogger<ProductService>.Instance, db, null), db);

        var result = await controller.VerifyPurchase(new GooglePlayPurchaseRequest
        {
            ProductId = "coins", PackageName = "package", PurchaseToken = "token", UserId = requestUser
        });

        Assert.That(result.Result, valid ? Is.TypeOf<OkObjectResult>() : Is.TypeOf<BadRequestObjectResult>());
        Assert.That(((ObjectResult)result.Result).Value, Is.TypeOf<GooglePlayPurchaseResponse>().With.Property("IsValid").EqualTo(valid));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
        Assert.That(user.Balance, Is.EqualTo(100));
        publisher.Verify(p => p.AcknowledgeProductPurchaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        payments.VerifyNoOtherCalls();
    }
}
