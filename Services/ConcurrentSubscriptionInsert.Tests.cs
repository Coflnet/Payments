using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Coflnet.Payments.Services;

/// <summary>
/// Simulates the race of a provider's near-simultaneous webhooks for a new subscription: once armed, the next save that
/// inserts a subscription row is preceded by another context committing the same row (same provider and external id),
/// as a concurrent handler does. The intercepted save then hits the unique index (ProviderSlug, ExternalId).
/// </summary>
public sealed class ConcurrentSubscriptionInsert(SqliteConnection connection) : SaveChangesInterceptor
{
    private bool armed;

    /// <summary>The rival inserts before the next subscription insert of the intercepted context</summary>
    public void Arm() => armed = true;

    /// <summary>Whether the rival inserted its row</summary>
    public bool Raced { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var candidate = eventData.Context?.ChangeTracker.Entries<UserSubscription>()
            .FirstOrDefault(e => e.State == EntityState.Added)?.Entity;
        if (!armed || candidate == null)
            return result;
        armed = false;
        await InsertTheSameRow(candidate);
        Raced = true;
        return result;
    }

    private async Task InsertTheSameRow(UserSubscription candidate)
    {
        await using var rival = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        rival.Subscriptions.Add(new UserSubscription
        {
            User = await rival.Users.SingleAsync(u => u.ExternalId == candidate.User.ExternalId),
            Product = await rival.Set<Product>().SingleAsync(p => p.Id == candidate.Product.Id),
            ProviderSlug = candidate.ProviderSlug, ExternalId = candidate.ExternalId, Status = candidate.Status,
            PriceCents = candidate.PriceCents, Currency = candidate.Currency, CheckoutCountry = candidate.CheckoutCountry,
            ManagedPayments = candidate.ManagedPayments, CreatedAt = candidate.CreatedAt, RenewsAt = candidate.RenewsAt,
            UpdatedAt = DateTime.UtcNow
        });
        await rival.SaveChangesAsync();
    }
}
