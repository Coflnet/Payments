using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class ProductTitleLookupTests
{
    private static async Task<(PaymentContext, SqliteConnection)> CreateDb()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return (db, connection);
    }

    [Test]
    public async Task ReturnsTheTitleOfATopUpProduct()
    {
        var (db, connection) = await CreateDb();
        using var _ = connection;
        db.TopUpProducts.Add(new TopUpProduct { Id = 153, Title = "1,800 CoflCoins", Slug = "c_cc_1800", Cost = 1800, Price = 9.69m, CurrencyCode = "EUR", ProviderSlug = "stripe" });
        await db.SaveChangesAsync();

        Assert.That(await db.GetProductTitleAsync(153), Is.EqualTo("1,800 CoflCoins"));
    }

    [Test]
    public async Task ReturnsNullForUnknownProducts()
    {
        var (db, connection) = await CreateDb();
        using var _ = connection;

        Assert.That(await db.GetProductTitleAsync(999), Is.Null);
    }
}
