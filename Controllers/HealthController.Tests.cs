using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Payments.Controllers;

[TestFixture]
public class HealthControllerTests
{
    private static PaymentContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);

    [Test]
    public async Task ReadyReturns200WhenDatabaseAnswers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);

        var result = await new HealthController(db).Ready();

        Assert.That(result, Is.TypeOf<OkResult>());
    }

    [Test]
    public async Task ReadyReturns503WhenDatabaseCheckFails()
    {
        await using var connection = new SqliteConnection("Data Source=/nonexistent-dir/x.db;Mode=ReadOnly");
        await using var db = Context(connection);

        var result = await new HealthController(db).Ready();

        Assert.That(result, Is.TypeOf<StatusCodeResult>().With.Property("StatusCode").EqualTo(503));
    }
}
