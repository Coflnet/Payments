using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Payments.Controllers;
using Prometheus;

namespace Coflnet.Payments.Services;

public class PaymentMetricServiceTests
{
    [Test]
    public async Task ActiveOwnersPreservesLegacyCohortAndOnlyPublishesSuccessfulCounts()
    {
        RuntimeHelpers.RunClassConstructor(typeof(PaymentMetricService).TypeHandle);
        var initial = await Scrape();
        Assert.That(initial, Does.Not.Contain("payments_active_product_owners{product="));
        Assert.That(initial, Does.Not.Contain("payments_active_product_owners_last_success_timestamp_seconds{product="));

        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var direct = CreateProduct("premium_plus");
        var grouped = CreateProduct("premium_plus_year", new Group { Slug = "premium_plus" });
        grouped.Type = Product.ProductType.COLLECTABLE;
        var unrelated = CreateProduct("premium", new Group { Slug = "unrelated" });
        var owner = new User { ExternalId = "both-direct-and-group" };
        db.OwnerShips.AddRange(
            new OwnerShip { User = owner, Product = direct, Expires = now.AddHours(1) },
            new OwnerShip { User = owner, Product = grouped, Expires = now.AddHours(2) },
            new OwnerShip { User = new User { ExternalId = "group-only" }, Product = grouped, Expires = now.AddHours(1) },
            new OwnerShip { User = new User { ExternalId = "expired" }, Product = direct, Expires = now.AddSeconds(-1) },
            new OwnerShip { User = new User { ExternalId = "exact-boundary" }, Product = grouped, Expires = now },
            new OwnerShip { User = new User { ExternalId = "unrelated" }, Product = unrelated, Expires = now.AddHours(1) },
            // GroupBy(UserId) preserves the existing endpoint's single nullable-owner group.
            new OwnerShip { Product = direct, Expires = now.AddHours(1) },
            new OwnerShip { Product = grouped, Expires = now.AddHours(1) });
        db.TierSlots.Add(new TierSlot
        {
            User = new User { ExternalId = "tier-slot-only" }, Tier = "premium_plus", Expires = now.AddHours(1)
        });
        await db.SaveChangesAsync();

        await PaymentMetricService.RefreshActiveProductOwners(db, now, CancellationToken.None);
        var scrape = await Scrape();
        Assert.That(Value(scrape, "payments_active_product_owners"), Is.EqualTo(3));
        var legacyCount = await new ProductsController(NullLogger<ProductsController>.Instance, db, null, null)
            .GetOwnerCount("premium_plus");
        Assert.That(Value(scrape, "payments_active_product_owners"), Is.EqualTo(legacyCount));
        Assert.That(Value(scrape, "payments_active_product_owners_last_success_timestamp_seconds"),
            Is.InRange(new DateTimeOffset(now).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        await db.Database.ExecuteSqlRawAsync("DROP TABLE OwnerShip");
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        Assert.ThrowsAsync<SqliteException>(() => PaymentMetricService.RefreshActiveProductOwners(db, now, CancellationToken.None));
        var failedScrape = await Scrape();
        Assert.That(Value(failedScrape, "payments_active_product_owners"), Is.EqualTo(3));
        Assert.That(Value(failedScrape, "payments_active_product_owners_last_success_timestamp_seconds"),
            Is.EqualTo(Value(scrape, "payments_active_product_owners_last_success_timestamp_seconds")),
            "Failure must not advance the success timestamp");

        // A valid empty cohort must publish a real zero, unlike a failed refresh.
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        await PaymentMetricService.RefreshActiveProductOwners(db, now, CancellationToken.None);
        Assert.That(Value(await Scrape(), "payments_active_product_owners"), Is.Zero);
    }

    private static PurchaseableProduct CreateProduct(string slug, params Group[] groups) => new()
    {
        Slug = slug, Title = slug, Description = slug, Groups = new List<Group>(groups), Type = Product.ProductType.SERVICE
    };

    private static async Task<string> Scrape()
    {
        using var output = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static double Value(string scrape, string metric) => double.Parse(
        scrape.Split('\n').Single(line => line.StartsWith(metric + "{product=\"premium_plus\"} ", StringComparison.Ordinal))
            .Split(' ')[1], CultureInfo.InvariantCulture);
}
