using System;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;

namespace Coflnet.Payments.Configuration;

public class CockroachHistoryRepositoryTests
{
    [Test]
    public void NpgsqlContext_CanReplaceMigrationLockWithCockroachRowLock()
    {
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .ReplaceService<IHistoryRepository, CockroachHistoryRepository>()
            .Options;

        using var context = new PaymentContext(options);
        var historyRepository = context.GetService<IHistoryRepository>();

        Assert.That(historyRepository, Is.TypeOf<CockroachHistoryRepository>());
        Assert.That(
            ((CockroachHistoryRepository)historyRepository).GetMigrationLockSql(),
            Is.EqualTo(
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" " +
                "ORDER BY \"MigrationId\" LIMIT 1 FOR UPDATE"));
    }

    [Test]
    public async Task RowLock_SerializesConcurrentMigrationRunners_OnCockroachDb()
    {
        var connectionString = Environment.GetEnvironmentVariable("COCKROACH_TEST_CONNECTION");
        if (string.IsNullOrEmpty(connectionString))
            Assert.Ignore("Set COCKROACH_TEST_CONNECTION to run the CockroachDB integration test.");

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseNpgsql(connectionString)
            .ReplaceService<IHistoryRepository, CockroachHistoryRepository>()
            .Options;

        await using var firstContext = new PaymentContext(options);
        await firstContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" VARCHAR(150) PRIMARY KEY,
                "ProductVersion" VARCHAR(32) NOT NULL
            );
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('lock-test', '10.0.7')
            ON CONFLICT ("MigrationId") DO NOTHING;
            """);

        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();
        await using var firstLock = await firstContext
            .GetService<IHistoryRepository>()
            .AcquireDatabaseLockAsync();

        await using var secondContext = new PaymentContext(options);
        await using var secondTransaction = await secondContext.Database.BeginTransactionAsync();
        var secondLockTask = secondContext
            .GetService<IHistoryRepository>()
            .AcquireDatabaseLockAsync();

        await Task.Delay(300);
        Assert.That(secondLockTask.IsCompleted, Is.False, "The second migration runner must wait for the first.");

        await firstTransaction.CommitAsync();
        await using var secondLock = await secondLockTask.WaitAsync(TimeSpan.FromSeconds(5));
        await secondTransaction.RollbackAsync();
    }
}
