using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class ServicePerformanceDeclarationTests
{
    private static readonly DateTime Now = new(
        2026,
        8,
        8,
        8,
        0,
        0,
        DateTimeKind.Utc);
    private SqliteConnection connection;
    private PaymentContext db;
    private UserService users;
    private PurchaseableProduct premium;

    [SetUp]
    public async Task SetUp()
    {
        connection = new("Filename=:memory:");
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
        users = new(NullLogger<UserService>.Instance, db);
        premium = new()
        {
            Title = "Premium week",
            Slug = "premium-week",
            Cost = 5,
            OwnershipSeconds = (long)TimeSpan.FromDays(7).TotalSeconds,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group
        {
            Slug = premium.Slug,
            Products = [premium]
        };
        premium.Groups.Add(group);
        db.Groups.Add(group);
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Test]
    public async Task AffirmativeDeclarationIsPersistedWithExactOrderEvidence()
    {
        var user = await Fund("declared-user");
        var service = CreateService();
        var request = Request("declared-order");

        await service.PurchaseServiceDeclared(
            premium.Slug,
            user.ExternalId,
            request);

        var evidence = await db.ServicePerformanceDeclarations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(evidence.UserId, Is.EqualTo(user.ExternalId));
            Assert.That(evidence.ProductId, Is.EqualTo(premium.Id));
            Assert.That(evidence.ProductSlug, Is.EqualTo(premium.Slug));
            Assert.That(evidence.Count, Is.EqualTo(1));
            Assert.That(evidence.PurchaseReference,
                Is.EqualTo(request.Reference));
            Assert.That(evidence.CoinAmount, Is.EqualTo(5));
            Assert.That(evidence.StartsAtUtc, Is.EqualTo(Now));
            Assert.That(evidence.EndsAtUtc,
                Is.EqualTo(Now.AddDays(7)));
            Assert.That(evidence.DeclarationRequired, Is.True);
            Assert.That(evidence.EarlyPerformanceRequested, Is.True);
            Assert.That(
                evidence.WithdrawalConsequenceAcknowledged,
                Is.True);
            Assert.That(evidence.Locale, Is.EqualTo("de"));
            Assert.That(evidence.DeclarationVersion,
                Is.EqualTo("premium-service-start-v1"));
            Assert.That(evidence.DeclarationText,
                Does.Contain("complete performance"));
            Assert.That(evidence.DeclarationSha256,
                Is.EqualTo(request.DeclarationSha256));
            Assert.That(evidence.AgreementId,
                Is.EqualTo("skycofl"));
            Assert.That(evidence.AgreementHash,
                Is.EqualTo(request.AgreementHash));
            Assert.That(evidence.WithdrawalVersion,
                Is.EqualTo("withdrawal-v1"));
        });
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
        var queued = await db.PaymentConfirmationOutbox.SingleAsync();
        var confirmation = JsonConvert.DeserializeObject<PaymentEvent>(
            queued.Payload);
        Assert.Multiple(() =>
        {
            Assert.That(confirmation.ConfirmationType,
                Is.EqualTo("service_purchase"));
            Assert.That(confirmation.CoinAmount, Is.EqualTo(5));
            Assert.That(confirmation.LegalLocale, Is.EqualTo("de"));
            Assert.That(confirmation.AgreementId, Is.EqualTo("skycofl"));
            Assert.That(confirmation.AgreementHash,
                Is.EqualTo(request.AgreementHash));
            Assert.That(confirmation.WithdrawalSha256,
                Is.EqualTo(request.WithdrawalSha256));
        });
    }

    [Test]
    public async Task LegacyAndFalseDeclarationsContinueWhileEnforcementIsOff()
    {
        var logger = new Mock<ILogger<TransactionService>>();
        var legacy = await Fund("legacy-user");
        var falseDeclaration = await Fund("false-user");
        var service = CreateService(logger: logger.Object);

        await service.PurchaseServie(
            premium.Slug,
            legacy.ExternalId,
            1,
            "legacy-order");
        await service.PurchaseServiceDeclared(
            premium.Slug,
            falseDeclaration.ExternalId,
            new()
            {
                Reference = "false-order",
                Count = 1,
                Locale = "en",
                DeclarationVersion = "displayed-version",
                RequestId = Guid.NewGuid().ToString()
            });

        Assert.That(await db.ServicePerformanceDeclarations.CountAsync(),
            Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(2));
        Assert.That(logger.Invocations.Count(invocation =>
            invocation.Arguments[0] is LogLevel.Warning
            && invocation.Arguments[2].ToString()!.Contains(
                "Early-performance declaration rollout would block")),
            Is.EqualTo(2));
    }

    [Test]
    public async Task OldDeclaredClientContinuesWithoutMislabelledRootEvidenceWhileEnforcementIsOff()
    {
        var logger = new Mock<ILogger<TransactionService>>();
        var user = await Fund("old-declared-user");
        var request = Request("old-declared-order");
        request.AgreementId = null;
        request.AgreementHash = null;

        await CreateService(logger: logger.Object).PurchaseServiceDeclared(
            premium.Slug,
            user.ExternalId,
            request);

        Assert.That(await db.ServicePerformanceDeclarations.CountAsync(),
            Is.Zero);
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
        Assert.That(logger.Invocations.Any(invocation =>
            invocation.Arguments[0] is LogLevel.Warning
            && invocation.Arguments[2].ToString()!.Contains(
                "omitted SkyCofl Agreement Root evidence")), Is.True);

        var enforcedUser = await Fund("old-declared-enforced-user");
        request.Reference = "old-declared-enforced-order";
        request.RequestId = Guid.NewGuid().ToString();
        Assert.That(
            Assert.ThrowsAsync<ApiException>(() => CreateService(enforce: true)
                .PurchaseServiceDeclared(
                    premium.Slug,
                    enforcedUser.ExternalId,
                    request))?.Message,
            Is.EqualTo("invalid_service_performance_declaration"));
    }

    [Test]
    public async Task AffirmativeDeclarationRequiresMatchingTextHash()
    {
        var user = await Fund("invalid-evidence-user");
        var request = Request("invalid-evidence-order");
        request.DeclarationSha256 = new string('0', 64);

        Assert.That(
            Assert.ThrowsAsync<ApiException>(() => CreateService()
                .PurchaseServiceDeclared(
                    premium.Slug,
                    user.ExternalId,
                    request))?.Message,
            Is.EqualTo("invalid_service_performance_declaration"));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
        Assert.That(await db.ServicePerformanceDeclarations.CountAsync(),
            Is.Zero);
    }

    [Test]
    public async Task EnforcementSwitchRejectsMissingOrFalseDeclaration()
    {
        var legacy = await Fund("blocked-legacy");
        var falseDeclaration = await Fund("blocked-false");
        var service = CreateService(enforce: true);

        Assert.That(
            Assert.ThrowsAsync<ApiException>(() => service.PurchaseServie(
                premium.Slug,
                legacy.ExternalId,
                1,
                "blocked-legacy"))?.Message,
            Is.EqualTo("service_performance_declaration_required"));
        Assert.That(
            Assert.ThrowsAsync<ApiException>(() =>
                service.PurchaseServiceDeclared(
                    premium.Slug,
                    falseDeclaration.ExternalId,
                    new()
                    {
                        Reference = "blocked-false",
                        Count = 1,
                        Locale = "en",
                        RequestId = Guid.NewGuid().ToString()
                    }))?.Message,
            Is.EqualTo("service_performance_declaration_required"));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task MatchingRetryPrecedesValidationAndFieldChangesConflict()
    {
        var user = await Fund("retry-user");
        var request = Request("retry-order");
        await CreateService()
            .PurchaseServiceDeclared(premium.Slug, user.ExternalId, request);
        user.Balance = 0;
        await db.SaveChangesAsync();
        var service = CreateService();

        Assert.DoesNotThrowAsync(() => service.PurchaseServiceDeclared(
            premium.Slug,
            user.ExternalId,
            request));

        Assert.That(await db.PaymentConfirmationOutbox.CountAsync(),
            Is.EqualTo(1));

        foreach (var changed in ChangedRequests(request))
            Assert.That(
                Assert.ThrowsAsync<ApiException>(() =>
                    service.PurchaseServiceDeclared(
                        premium.Slug,
                        user.ExternalId,
                        changed))?.Message,
                Is.EqualTo("service declaration request id already used"));

        var otherUser = await Fund("other-user");
        Assert.That(
            Assert.ThrowsAsync<ApiException>(() =>
                service.PurchaseServiceDeclared(
                    premium.Slug,
                    otherUser.ExternalId,
                    request))?.Message,
            Is.EqualTo("service declaration request id already used"));
        Assert.That(await db.FiniteTransactions.CountAsync(), Is.EqualTo(1));
    }

    private async Task<User> Fund(string id)
    {
        var user = await users.GetOrCreate(id);
        user.Balance = 10;
        await db.SaveChangesAsync();
        return user;
    }

    private TransactionService CreateService(
        bool enforce = false,
        ILogger<TransactionService> logger = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["TRANSFER:LIMIT"] = "12",
                ["TRANSFER:PeriodDays"] = "10",
                ["LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION"] =
                    enforce.ToString()
            })
            .Build();
        return new(
            logger ?? NullLogger<TransactionService>.Instance,
            db,
            users,
            new NoopTransactionProducer(),
            config,
            new RuleEngine(NullLogger<RuleEngine>.Instance, db),
            new FixedTimeProvider(new DateTimeOffset(Now)));
    }

    private static ServicePurchaseRequest Request(string reference) => new()
    {
        Reference = reference,
        Count = 1,
        ImmediatePerformanceRequested = true,
        WithdrawalConsequenceAcknowledged = true,
        Locale = "de-DE",
        DeclarationVersion = "premium-service-start-v1",
        DeclarationText =
            "The withdrawal right ends only after complete performance.",
        DeclarationSha256 = Hash(
            "The withdrawal right ends only after complete performance."),
        AgreementId = "skycofl",
        AgreementHash = new string('a', 64),
        WithdrawalVersion = "withdrawal-v1",
        WithdrawalSha256 = new string('c', 64),
        RequestId = Guid.NewGuid().ToString()
    };

    private static IEnumerable<ServicePurchaseRequest> ChangedRequests(
        ServicePurchaseRequest original)
    {
        yield return Copy(original, count: 2);
        yield return Copy(original, reference: "changed-reference");
        yield return Copy(original, early: false, acknowledged: false);
        yield return Copy(original, locale: "en");
        yield return Copy(original, version: "changed-version");
        yield return Copy(original, text: "changed text");
        yield return Copy(original, declarationHash: new string('d', 64));
        yield return Copy(original, agreementId: "changed-service");
        yield return Copy(original, agreementHash: new string('e', 64));
        yield return Copy(original, withdrawalVersion: "changed-withdrawal");
        yield return Copy(original, withdrawalHash: new string('0', 64));
    }

    private static ServicePurchaseRequest Copy(
        ServicePurchaseRequest source,
        int? count = null,
        string reference = null,
        bool? early = null,
        bool? acknowledged = null,
        string locale = null,
        string version = null,
        string text = null,
        string declarationHash = null,
        string agreementId = null,
        string agreementHash = null,
        string withdrawalVersion = null,
        string withdrawalHash = null) => new()
    {
        Reference = reference ?? source.Reference,
        Count = count ?? source.Count,
        ImmediatePerformanceRequested =
            early ?? source.ImmediatePerformanceRequested,
        WithdrawalConsequenceAcknowledged =
            acknowledged ?? source.WithdrawalConsequenceAcknowledged,
        Locale = locale ?? source.Locale,
        DeclarationVersion = version ?? source.DeclarationVersion,
        DeclarationText = text ?? source.DeclarationText,
        DeclarationSha256 =
            declarationHash ?? source.DeclarationSha256,
        AgreementId = agreementId ?? source.AgreementId,
        AgreementHash = agreementHash ?? source.AgreementHash,
        WithdrawalVersion =
            withdrawalVersion ?? source.WithdrawalVersion,
        WithdrawalSha256 =
            withdrawalHash ?? source.WithdrawalSha256,
        RequestId = source.RequestId
    };

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NoopTransactionProducer : ITransactionEventProducer
    {
        public Task ProduceEvent(TransactionEvent transactionEvent) =>
            Task.CompletedTask;
    }
}
