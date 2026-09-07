using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Threading;

namespace Coflnet.Payments.Services;

public class TierSlotServiceTests
{
    private SqliteConnection connection;
    private PaymentContext db;
    private UserService users;
    private TransactionService transactions;
    private TierSlotService slots;
    private PurchaseableProduct bundle;
    private ReadCounter reads;
    private const string Minecraft = "123456781234123412341234567890ab";

    [SetUp]
    public async Task Setup()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        reads = new ReadCounter();
        db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        users = new UserService(NullLogger<UserService>.Instance, db);
        transactions = new TransactionService(NullLogger<TransactionService>.Instance, db, users,
            new ProductsServiceTests.NullTransationProducer(), null, new RuleEngine(NullLogger<RuleEngine>.Instance, db));
        slots = new TierSlotService(db);
        var products = new ProductService(NullLogger<ProductService>.Instance, db,
            new GroupService(NullLogger<GroupService>.Instance, db));
        bundle = new PurchaseableProduct { Slug = "premium_plus-slots-3", Title = "Three slots", Cost = 24429,
            OwnershipSeconds = 2419200, SlotCount = 3, SlotTier = "premium_plus", Type = Product.ProductType.SERVICE };
        await products.UpdateOrAddProduct(bundle);
        await products.UpdateOrAddProduct(new PurchaseableProduct { Slug = "revert", Title = "Refund", Type = Product.ProductType.SERVICE });
        var owner = await users.GetOrCreate("owner");
        owner.Balance = 100000;
        owner.Country = "DE";
        await users.GetOrCreate("friend");
        await users.GetOrCreate("other");
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Teardown()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private Task Buy(string reference = "purchase", int count = 1) =>
        transactions.PurchaseServie(bundle.Slug, "owner", count, reference);

    [Test]
    public async Task BundleCreatesConcurrentUnassignedCapacityAndChargesOnlyOwner()
    {
        var before = DateTime.UtcNow;
        await Buy();
        var owned = await slots.GetOwned("owner");
        Assert.That(owned, Has.Length.EqualTo(3));
        Assert.That(owned.All(s => s.Expires >= before.AddDays(28) && s.Expires < DateTime.UtcNow.AddDays(28)), Is.True);
        Assert.That(owned.All(s => s.OwnerId == "owner" && s.CanManage && s.AssignedUserId == null), Is.True);
        Assert.That(await db.FiniteTransactions.Select(t => t.User.ExternalId).SingleAsync(), Is.EqualTo("owner"));
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(75571));
        Assert.That((await users.GetOrCreate("owner")).Country, Is.EqualTo("DE"));
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await slots.GetAccess("owner"), Is.Empty);
    }

    [Test]
    public async Task MorePackagesIncreaseCapacityRatherThanDuration()
    {
        await Buy(count: 2);
        Assert.That(await slots.GetOwned("owner"), Has.Length.EqualTo(6));
        Assert.That((await slots.GetOwned("owner")).All(s => s.Expires < DateTime.UtcNow.AddDays(29)), Is.True);
    }

    [Test]
    public async Task FriendReceivesAccessWithoutBillingEvidenceOrOwnership()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        var access = (await slots.GetAccess("friend")).Single();
        Assert.That(access.CanManage, Is.False);
        Assert.That(access.OwnerId, Is.EqualTo("owner"));
        Assert.That(await slots.GetOwned("friend"), Is.Empty);
        Assert.That((await users.GetOrCreate("friend")).Balance, Is.Zero);
        Assert.ThrowsAsync<ApiException>(() => slots.Assign("friend", slot.Id, new() { UserId = "other", Version = access.Version }));
    }

    [Test]
    public async Task MinecraftAssignmentDoesNotGrantAccessToOtherAltsOnSameEmail()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "owner",
            MinecraftUuid = Guid.Parse(Minecraft).ToString("D").ToUpperInvariant(), Version = slot.Version });
        Assert.That(await slots.GetAccess("owner"), Is.Empty);
        Assert.That(await slots.GetAccess("owner", Minecraft), Has.Length.EqualTo(1));
        Assert.That(await slots.GetAccess(null, Minecraft), Has.Length.EqualTo(1));
        Assert.That(await slots.GetAccess("owner", "ffffffffffffffffffffffffffffffff"), Is.Empty);
        Assert.That(await slots.GetAccess("friend", Minecraft), Is.Empty);
    }

    [Test]
    public async Task UuidOnlyAssignmentSupportsDifferentLoginAccounts()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { MinecraftUuid = Minecraft, Version = slot.Version });
        Assert.That(await slots.GetAccess(null, Minecraft), Has.Length.EqualTo(1));
        Assert.That(await slots.GetAccess("friend", Minecraft), Has.Length.EqualTo(1));
        Assert.That(await slots.GetAccess("friend"), Is.Empty);
    }

    [Test]
    public async Task ReassignmentAndReleaseRevokePreviousAccessWithoutChangingExpiry()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        await slots.Assign("owner", slot.Id, new() { UserId = "other", Version = slot.Version + 1 });
        Assert.That(await slots.GetAccess("friend"), Is.Empty);
        Assert.That((await slots.GetAccess("other")).Single().Expires, Is.EqualTo(slot.Expires));
        await slots.Assign("owner", slot.Id, new() { Version = slot.Version + 2 });
        Assert.That(await slots.GetAccess("other"), Is.Empty);
    }

    [Test]
    public async Task StaleAssignmentCannotOverwriteAConcurrentChange()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        Assert.ThrowsAsync<ApiException>(() => slots.Assign("owner", slot.Id, new() { UserId = "other", Version = slot.Version }));
        Assert.That(await slots.GetAccess("friend"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task DuplicatePurchaseCannotChargeOrGrantTwice()
    {
        await Buy();
        Assert.ThrowsAsync<TransactionService.DupplicateTransactionException>(() => Buy());
        db.ChangeTracker.Clear();
        Assert.That(await slots.GetOwned("owner"), Has.Length.EqualTo(3));
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(75571));
    }

    [Test]
    public async Task InsufficientFundsRollBackCapacityAndTransaction()
    {
        Assert.ThrowsAsync<ApiException>(() => Buy(count: 5));
        db.ChangeTracker.Clear();
        Assert.That(await db.TierSlots.CountAsync(), Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(100000));
    }

    private ServicePurchaseRequest Extension(long[] ids, string reference = "extend") => new()
    {
        Reference = reference, SlotIds = ids, Locale = "en", RequestId = Guid.NewGuid().ToString()
    };

    [Test]
    public async Task RenewalExtendsStableOwnedSlotsAndPreservesAssignments()
    {
        await Buy();
        var owned = await slots.GetOwned("owner");
        await slots.Assign("owner", owned[0].Id, new() { UserId = "friend", Version = owned[0].Version });
        await transactions.PurchaseServiceDeclared(bundle.Slug, "owner", Extension(owned.Select(s => s.Id).ToArray()));
        var renewed = await slots.GetOwned("owner");
        Assert.That(renewed, Has.Length.EqualTo(3));
        Assert.That(renewed[0].Expires, Is.EqualTo(owned[0].Expires.AddDays(28)));
        Assert.That(renewed[0].AssignedUserId, Is.EqualTo("friend"));
        Assert.That(await db.TierSlotGrants.CountAsync(), Is.EqualTo(6));
    }

    [Test]
    public async Task RefundRevokesReassignedSlotsAndIsIdempotent()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        var id = await db.FiniteTransactions.Select(t => t.Id).SingleAsync();
        await transactions.RevertPurchase("owner", id);
        await transactions.RevertPurchase("owner", id);
        Assert.That(await db.OwnerShips.CountAsync(), Is.Zero);
        Assert.That(await slots.GetAccess("friend"), Is.Empty);
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(100000));
        Assert.That((await users.GetOrCreate("friend")).Balance, Is.Zero);
    }

    [Test]
    public async Task RecipientCannotRefundOwnersPurchase()
    {
        await Buy();
        var id = await db.FiniteTransactions.Select(t => t.Id).SingleAsync();
        Assert.ThrowsAsync<ApiException>(() => transactions.RevertPurchase("friend", id));
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(75571));
    }

    [Test]
    public async Task ForeignAndRepeatedSlotIdsCannotBeExtended()
    {
        await Buy();
        var owned = await slots.GetOwned("owner");
        var other = await users.GetOrCreate("other");
        other.Balance = 100000;
        await db.SaveChangesAsync();
        Assert.ThrowsAsync<ApiException>(() => transactions.PurchaseServiceDeclared(bundle.Slug, "other", Extension(owned.Select(s => s.Id).ToArray())));
        db.ChangeTracker.Clear();
        Assert.ThrowsAsync<ApiException>(() => transactions.PurchaseServiceDeclared(bundle.Slug, "owner", Extension([owned[0].Id, owned[0].Id, owned[0].Id])));
        db.ChangeTracker.Clear();
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
        Assert.That((await users.GetOrCreate("other")).Balance, Is.EqualTo(100000));
    }

    [Test]
    public async Task ExpiredCapacityAndMalformedUuidsDoNotGrantAccess()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        Assert.ThrowsAsync<ApiException>(() => slots.Assign("owner", slot.Id, new() { MinecraftUuid = "anything", Version = slot.Version }));
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        (await db.TierSlots.FindAsync(slot.Id)).Expires = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.That(await slots.GetAccess("friend"), Is.Empty);
    }

    [Test]
    public async Task ExistingOwnershipEndpointsIncludeDelegationWithoutExtendingPurchasedTime()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        var controller = new global::Payments.Controllers.UserController(
            NullLogger<global::Payments.Controllers.UserController>.Instance, db, transactions, users, null);
        var slugs = new System.Collections.Generic.HashSet<string> { "premium_plus", "premium", "starter_premium", "unrelated" };
        var lookup = await controller.GetAllOwnershipsLookup("friend", slugs);
        Assert.That(lookup.Keys, Is.EquivalentTo(new[] { "premium_plus", "premium", "starter_premium" }));
        Assert.That(lookup.Values.All(expiry => expiry == slot.Expires), Is.True);
        Assert.That(await controller.Get("friend", "premium"), Is.EqualTo(slot.Expires));
        Assert.That(await controller.GetLongest("friend", slugs), Is.EqualTo(slot.Expires));
        Assert.That((await controller.GetAllOwnerships("friend", slugs)).All(o => o.SlotId == slot.Id && !o.CanManage), Is.True);
        Assert.That(await users.GetLongest("friend", slugs), Is.EqualTo(default(DateTime)), "Billing calculations must remain owner-only");
        await slots.Assign("owner", slot.Id, new() { Version = slot.Version + 1 });
        Assert.That(await controller.GetAllOwnershipsLookup("friend", slugs), Is.Empty);
    }

    [Test]
    public async Task ExistingLookupRestrictsMinecraftSlotsToTheRequestedUuid()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { MinecraftUuid = Minecraft, Version = slot.Version });
        Assert.That(await users.GetAccessUntil("friend", new() { "premium_plus" }), Is.Empty);
        Assert.That((await users.GetAccessUntil("friend", new() { "premium_plus" }, Minecraft))["premium_plus"], Is.EqualTo(slot.Expires));
    }

    [Test]
    public async Task BulkExpiryLookupUsesOneDatabaseReadForAllTiersAndSlots()
    {
        await Buy();
        var slot = (await slots.GetOwned("owner"))[0];
        await slots.Assign("owner", slot.Id, new() { UserId = "friend", Version = slot.Version });
        reads.Count = 0;
        var access = await users.GetAccessUntil("friend", new() { "premium", "premium_plus", "starter_premium" });
        Assert.That(access.Count, Is.EqualTo(3));
        Assert.That(reads.Count, Is.EqualTo(1));
        Assert.That(reads.LastSql, Does.Contain("UNION ALL").And.Contain("MAX("));
    }

    [Test]
    public async Task RefundingAnExtensionPreservesTheOriginalPaidPeriod()
    {
        await Buy();
        var owned = await slots.GetOwned("owner");
        await transactions.PurchaseServiceDeclared(bundle.Slug, "owner", Extension(owned.Select(s => s.Id).ToArray()));
        var renewal = await db.FiniteTransactions.Where(t => t.Reference == "extend").Select(t => t.Id).SingleAsync();
        await transactions.RevertPurchase("owner", renewal);
        Assert.That((await slots.GetOwned("owner"))[0].Expires, Is.EqualTo(owned[0].Expires));
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(75571));
    }

    [Test]
    public async Task DeclaredSlotPurchaseRetriesReturnTheSameCapacityAndOwnerEvidence()
    {
        var declaration = Extension(null);
        declaration.ImmediatePerformanceRequested = true;
        declaration.WithdrawalConsequenceAcknowledged = true;
        declaration.DeclarationVersion = "v1";
        declaration.DeclarationText = "Begin the purchased service now.";
        declaration.DeclarationSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(declaration.DeclarationText))).ToLowerInvariant();
        declaration.AgreementId = "group-contract";
        declaration.AgreementHash = new string('a', 64);
        declaration.WithdrawalVersion = "v1";
        declaration.WithdrawalSha256 = new string('b', 64);
        await transactions.PurchaseServiceDeclared(bundle.Slug, "owner", declaration);
        await transactions.PurchaseServiceDeclared(bundle.Slug, "owner", declaration);
        Assert.That(await slots.GetOwned("owner"), Has.Length.EqualTo(3));
        Assert.That((await users.GetOrCreate("owner")).Balance, Is.EqualTo(75571));
        var evidence = await db.ServicePerformanceDeclarations.SingleAsync();
        Assert.That(evidence.UserId, Is.EqualTo("owner"));
        Assert.That(evidence.EndsAtUtc - evidence.StartsAtUtc, Is.EqualTo(TimeSpan.FromDays(28)));
        declaration.SlotIds = [long.MaxValue, long.MaxValue - 1, long.MaxValue - 2];
        Assert.ThrowsAsync<ApiException>(() => transactions.PurchaseServiceDeclared(bundle.Slug, "owner", declaration));
    }

    [Test]
    public async Task CatalogAppliesSlotCapacityChangesWithoutMutatingOldProducts()
    {
        var products = new ProductService(NullLogger<ProductService>.Instance, db,
            new GroupService(NullLogger<GroupService>.Instance, db));
        var replacement = new PurchaseableProduct { Slug = bundle.Slug, Title = bundle.Title,
            Cost = bundle.Cost, OwnershipSeconds = bundle.OwnershipSeconds, Type = bundle.Type,
            SlotCount = 4, SlotTier = bundle.SlotTier };
        await products.ApplyProductList([replacement]);
        Assert.That((await products.GetProduct("premium_plus-slots-3")).SlotCount, Is.EqualTo(4));
        Assert.That(bundle.SlotCount, Is.EqualTo(3));
        Assert.That(bundle.Type.HasFlag(Product.ProductType.DISABLED), Is.True);
    }

    private class ReadCounter : DbCommandInterceptor
    {
        public int Count;
        public string LastSql;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            LastSql = command.CommandText;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
