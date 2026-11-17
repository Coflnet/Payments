using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class CreatorCodeReversalTests
{
    private SqliteConnection connection;
    private PaymentContext db;
    private RaceOnFirstSave race;

    /// <summary>Writes the same reversal through another context right before the first save, like a concurrent webhook</summary>
    private sealed class RaceOnFirstSave(Func<Task> concurrentWrite) : SaveChangesInterceptor
    {
        private bool done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (done)
                return result;
            done = true;
            await concurrentWrite();
            return result;
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        race = new RaceOnFirstSave(() => Task.CompletedTask);
        db = NewContext(race);
        await db.Database.EnsureCreatedAsync();
        db.CreatorCodes.Add(new CreatorCode { Id = 1, Code = "CREATOR", CreatorUserId = "c", RevenueSharePercent = 10, CreatedAt = DateTime.UtcNow });
        db.CreatorCodeRevenues.Add(new CreatorCodeRevenue
            { CreatorCodeId = 1, UserId = "buyer", ProductId = 1, FinalPrice = 10, CreatorRevenue = 1, Currency = "EUR", TransactionReference = "in_1" });
        await db.SaveChangesAsync();
    }

    private PaymentContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);

    [TearDown]
    public async Task TearDown()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    /// <summary>
    /// Regression: two refund webhooks for the same payment both passed the "no reversal yet" check and each wrote a
    /// reversal row, reversing the share twice. The unique index on reversal references makes the second one "already done".
    /// </summary>
    [Test]
    public async Task AConcurrentReversalOfTheSameRefundIsTreatedAsAlreadyDone()
    {
        var rival = NewContext();
        db.Dispose();
        db = NewContext(new RaceOnFirstSave(async () =>
        {
            rival.CreatorCodeRevenues.Add(new CreatorCodeRevenue
            {
                CreatorCodeId = 1, UserId = "buyer", ProductId = 1, FinalPrice = -10, CreatorRevenue = -1, Currency = "EUR",
                TransactionReference = "in_1#refund:charge"
            });
            await rival.SaveChangesAsync();
        }));
        var service = new CreatorCodeService(NullLogger<CreatorCodeService>.Instance, db);

        await service.ReverseRevenueAsync("in_1", "charge", 1m);

        var rows = await rival.CreatorCodeRevenues.Where(r => r.CreatorRevenue < 0).ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows.Sum(r => r.CreatorRevenue), Is.EqualTo(-1m));
        await rival.DisposeAsync();
    }

    /// <summary>
    /// Regression: two refunds of one Stripe charge arrive as two charge.refunded events (cumulative 50% and 100%). When the
    /// 50% one won the race, the 100% one hit the unique index, was treated as "already done" and its remaining 50% was never
    /// reversed (the webhook succeeded, so Stripe did not retry). The conflict is re-read and the rest written as "+1".
    /// </summary>
    [Test]
    public async Task ALargerConcurrentRefundOfTheSameKeyStillReversesItsRemainder()
    {
        var rival = NewContext();
        db.Dispose();
        db = NewContext(new RaceOnFirstSave(async () =>
        {
            rival.CreatorCodeRevenues.Add(new CreatorCodeRevenue
            {
                CreatorCodeId = 1, UserId = "buyer", ProductId = 1, FinalPrice = -5, CreatorRevenue = -0.5m, Currency = "EUR",
                TransactionReference = "in_1#refund:charge"
            });
            await rival.SaveChangesAsync();
        }));
        var service = new CreatorCodeService(NullLogger<CreatorCodeService>.Instance, db);

        await service.ReverseRevenueAsync("in_1", "charge", 1m);

        var rows = await rival.CreatorCodeRevenues.Where(r => r.CreatorRevenue < 0).OrderBy(r => r.TransactionReference).ToListAsync();
        Assert.That(rows.Select(r => r.TransactionReference), Is.EqualTo(new[] { "in_1#refund:charge", "in_1#refund:charge+1" }));
        Assert.That((rows.Sum(r => r.CreatorRevenue), rows.Sum(r => r.FinalPrice)), Is.EqualTo((-1m, -10m)));
        await rival.DisposeAsync();
    }

    [Test]
    public async Task OnlyReversalReferencesAreUniqueSoHistoricDuplicateSalesStillLoad()
    {
        db.CreatorCodeRevenues.Add(new CreatorCodeRevenue
            { CreatorCodeId = 1, UserId = "buyer", ProductId = 1, CreatorRevenue = 1, Currency = "EUR", TransactionReference = "in_1" });
        await db.SaveChangesAsync();
        db.CreatorCodeRevenues.AddRange(
            new CreatorCodeRevenue { CreatorCodeId = 1, UserId = "b", ProductId = 1, Currency = "EUR", TransactionReference = "in_1#refund:x" },
            new CreatorCodeRevenue { CreatorCodeId = 1, UserId = "b", ProductId = 1, Currency = "EUR", TransactionReference = "in_1#refund:x" });

        var e = Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.That(CreatorCodeService.IsUniqueViolation(e), Is.True);
    }
}
