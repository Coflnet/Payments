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
    private const string ExpertMarketplaceAgreementHash =
        "9177b208e3226cd0974afdce79d4023520d69d65aaa34a8d86e04dd3e60f2401";
    private const string CreatorMarketplaceAgreementHash =
        "652e91d78ec3aa86dd1e7e33c1e1a81dc423c7ecc1b004466cae1733c9c4a280";
    private static readonly DateTime Now = new(
        2026,
        9,
        4,
        10,
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
    public async Task ExpertConfigUsesBuyerCountryQuoteAndQueuesOrderDetails()
    {
        var product = new PurchaseableProduct
        {
            Title = "Expert config purchase",
            Slug = "config-purchase",
            Cost = 600,
            OwnershipSeconds = 0,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group { Slug = product.Slug, Products = [product] };
        product.Groups.Add(group);
        db.Groups.Add(group);
        var user = await Fund("7");
        user.Country = "de";
        user.Balance = 1000;
        await db.SaveChangesAsync();
        var service = CreateService(now: Now.AddTicks(-1));
        var quote = await service.GetServicePurchaseQuote(
            product.Slug,
            user.ExternalId,
            1);
        var request = ExpertConfigRequest("config-order", quote);

        await service.PurchaseServiceDeclared(
            product.Slug,
            user.ExternalId,
            request);

        var evidence = await db.ServicePerformanceDeclarations.SingleAsync();
        var payment = JsonConvert.DeserializeObject<PaymentEvent>(
            (await db.PaymentConfirmationOutbox.SingleAsync()).Payload);
        Assert.Multiple(() =>
        {
            Assert.That(quote.TaxCountry, Is.EqualTo("DE"));
            Assert.That(quote.ConsumerRightsRegime, Is.EqualTo("EU"));
            Assert.That(quote.VatRateBasisPoints, Is.EqualTo(1900));
            Assert.That(quote.GrossEurCents, Is.EqualTo(223));
            Assert.That(quote.VatEurCents, Is.EqualTo(36));
            Assert.That(evidence.OrderDetailsJson,
                Is.EqualTo(request.OrderDetailsJson));
            Assert.That(payment.OrderDetailsJson,
                Is.EqualTo(request.OrderDetailsJson));
            Assert.That(payment.AgreementId,
                Is.EqualTo("expertMarketplace"));
            Assert.That(payment.ConsumerRightsRegime, Is.EqualTo("EU"));
        });
    }

    [Test]
    public async Task ExpertConfigRejectsGenericPurchaseRoute()
    {
        var product = AddExpertConfigProduct();
        var user = await Fund("7");

        Assert.ThrowsAsync<ApiException>(() => CreateService()
            .PurchaseProduct(product.Slug, user.ExternalId));
    }

    [Test]
    public async Task ExpertConfigRolloutAllowsOnlyUserSevenBeforeBoundary()
    {
        var product = AddExpertConfigProduct();
        var earlyUser = await Fund("7");
        earlyUser.Country = "DE";
        var blockedUser = await Fund("42");
        blockedUser.Country = "DE";
        var similarId = await Fund("07");
        similarId.Country = "DE";
        await db.SaveChangesAsync();
        var before = CreateService(now: Now.AddTicks(-1));

        var earlyQuote = await before.GetServicePurchaseQuote(
            product.Slug, earlyUser.ExternalId, 1);
        Assert.That(Assert.ThrowsAsync<ApiException>(() => before
                .GetServicePurchaseQuote(product.Slug, blockedUser.ExternalId, 1))
                ?.Message,
            Is.EqualTo("expert_config_not_available"));
        Assert.That(Assert.ThrowsAsync<ApiException>(() => before
                .GetServicePurchaseQuote(product.Slug, similarId.ExternalId, 1))
                ?.Message,
            Is.EqualTo("expert_config_not_available"));
        var blockedRequest = ExpertConfigRequest(
            "blocked-before-rollout", earlyQuote);
        Assert.That(Assert.ThrowsAsync<ApiException>(() => before
                .PurchaseServiceDeclared(
                    product.Slug, blockedUser.ExternalId, blockedRequest))
                ?.Message,
            Is.EqualTo("expert_config_not_available"));
        Assert.DoesNotThrowAsync(() => CreateService(now: Now)
            .GetServicePurchaseQuote(product.Slug, blockedUser.ExternalId, 1));
    }

    [TestCase("agreement")]
    [TestCase("order-agreement")]
    [TestCase("creator-agreement")]
    [TestCase("missing-order-roots")]
    public async Task ExpertConfigRejectsWrongFinalAgreementEvidenceForEarlyUser(
        string changedField)
    {
        var product = AddExpertConfigProduct();
        var user = await Fund("7");
        user.Country = "DE";
        user.Balance = 1000;
        await db.SaveChangesAsync();
        var service = CreateService(now: Now.AddTicks(-1));
        var quote = await service.GetServicePurchaseQuote(
            product.Slug, user.ExternalId, 1);
        var request = ExpertConfigRequest("wrong-root-order", quote);
        var wrong = new string('a', 64);
        if (changedField == "agreement")
            request.AgreementHash = wrong;
        else if (changedField == "order-agreement")
            request.OrderDetailsJson = OrderDetails(wrong);
        else if (changedField == "creator-agreement")
            request.OrderDetailsJson = OrderDetails(creatorHash: wrong);
        else
            request.OrderDetailsJson = "{}";

        Assert.That(Assert.ThrowsAsync<ApiException>(() => service
                .PurchaseServiceDeclared(product.Slug, user.ExternalId, request))
                ?.Message,
            Is.EqualTo("invalid_expert_config_order_evidence"));
        var transactionCount = await db.FiniteTransactions.CountAsync();
        var evidenceCount = await db.ServicePerformanceDeclarations.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(transactionCount, Is.Zero);
            Assert.That(evidenceCount, Is.Zero);
        });
    }

    [Test]
    public async Task ExistingExactExpertConfigRetryRemainsResumable()
    {
        var product = AddExpertConfigProduct();
        var user = await Fund("42");
        user.Country = "DE";
        user.Balance = 1000;
        await db.SaveChangesAsync();
        var service = CreateService(now: Now);
        var quote = await service.GetServicePurchaseQuote(
            product.Slug, user.ExternalId, 1);
        var request = ExpertConfigRequest("resumable-config-order", quote);
        await service.PurchaseServiceDeclared(
            product.Slug, user.ExternalId, request);
        user.Balance = 0;
        await db.SaveChangesAsync();

        Assert.DoesNotThrowAsync(() => CreateService(now: Now.AddTicks(-1))
            .PurchaseServiceDeclared(product.Slug, user.ExternalId, request));
        var transactionCount = await db.FiniteTransactions.CountAsync();
        var confirmationCount = await db.PaymentConfirmationOutbox.CountAsync();
        Assert.Multiple(() =>
        {
            Assert.That(transactionCount, Is.EqualTo(1));
            Assert.That(confirmationCount, Is.EqualTo(1));
        });
    }

    [TestCase("CA")]
    [TestCase("NO")]
    [TestCase(null)]
    public async Task ExpertConfigRejectsUnsupportedBuyerCountry(string country)
    {
        var product = new PurchaseableProduct
        {
            Title = "Expert config purchase",
            Slug = "config-purchase",
            Cost = 600,
            OwnershipSeconds = 0,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group { Slug = product.Slug, Products = [product] };
        product.Groups.Add(group);
        db.Groups.Add(group);
        var user = await Fund("unsupported-config-buyer");
        user.Country = country;
        await db.SaveChangesAsync();

        Assert.That(
            Assert.ThrowsAsync<ApiException>(() => CreateService()
                .GetServicePurchaseQuote(product.Slug, user.ExternalId, 1))
                ?.Message,
            Is.EqualTo("expert_config_tax_quote_unavailable"));
    }

    [TestCase("GB", "UK", 2000)]
    [TestCase("US", "US", 0)]
    public async Task ExpertConfigSelectsCountryRightsRegime(
        string country,
        string regime,
        int rate)
    {
        var product = new PurchaseableProduct
        {
            Title = "Expert config purchase",
            Slug = "config-purchase",
            Cost = 600,
            OwnershipSeconds = 0,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group { Slug = product.Slug, Products = [product] };
        product.Groups.Add(group);
        db.Groups.Add(group);
        var user = await Fund($"{country}-config-buyer");
        user.Country = country;
        if (country == "GB")
            db.PaymentRecords.Add(new PaymentRecord
            {
                ExternalUserId = user.ExternalId,
                UserId = user.Id,
                Country = country,
                ZipCode = "SW1A 1AA",
                Currency = "GBP",
                Provider = "test",
                PaidAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        var quote = await CreateService().GetServicePurchaseQuote(
            product.Slug, user.ExternalId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(quote.ConsumerRightsRegime, Is.EqualTo(regime));
            Assert.That(quote.VatRateBasisPoints, Is.EqualTo(rate));
        });
    }

    [TestCase(null)]
    [TestCase("BT1 1AA")]
    public async Task ExpertConfigRejectsNorthernIrelandOrUnknownGbPostcode(
        string postalCode)
    {
        var product = new PurchaseableProduct
        {
            Title = "Expert config purchase",
            Slug = "config-purchase",
            Cost = 600,
            OwnershipSeconds = 0,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group { Slug = product.Slug, Products = [product] };
        product.Groups.Add(group);
        db.Groups.Add(group);
        var user = await Fund("unsupported-gb-config-buyer");
        user.Country = "GB";
        if (postalCode != null)
            db.PaymentRecords.Add(new PaymentRecord
            {
                ExternalUserId = user.ExternalId,
                UserId = user.Id,
                Country = "GB",
                ZipCode = postalCode,
                Currency = "GBP",
                Provider = "test",
                PaidAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        Assert.That(
            Assert.ThrowsAsync<ApiException>(() => CreateService()
                .GetServicePurchaseQuote(product.Slug, user.ExternalId, 1))
                ?.Message,
            Is.EqualTo("expert_config_tax_quote_unavailable"));
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
        ILogger<TransactionService> logger = null,
        DateTime? now = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["TRANSFER:LIMIT"] = "12",
                ["TRANSFER:PeriodDays"] = "10",
                ["LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION"] =
                    enforce.ToString(),
                ["CONVERSION_RATE:Amount"] = "1802",
                ["CONVERSION_RATE:Eur"] = "6.69",
                ["EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:DE"] = "1900",
                ["EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:GB"] = "2000",
                ["EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:US"] = "0"
            })
            .Build();
        return new(
            logger ?? NullLogger<TransactionService>.Instance,
            db,
            users,
            new NoopTransactionProducer(),
            config,
            new RuleEngine(NullLogger<RuleEngine>.Instance, db),
            new FixedTimeProvider(new DateTimeOffset(now ?? Now)));
    }

    private PurchaseableProduct AddExpertConfigProduct()
    {
        var product = new PurchaseableProduct
        {
            Title = "Expert config purchase",
            Slug = "config-purchase",
            Cost = 600,
            OwnershipSeconds = 0,
            Type = Product.ProductType.SERVICE,
            Groups = []
        };
        var group = new Group { Slug = product.Slug, Products = [product] };
        product.Groups.Add(group);
        db.Groups.Add(group);
        return product;
    }

    private static ServicePurchaseRequest ExpertConfigRequest(
        string reference,
        ServicePurchaseQuote quote)
    {
        var request = Request(reference);
        request.AgreementId = "expertMarketplace";
        request.AgreementHash = ExpertMarketplaceAgreementHash;
        request.TaxCountry = quote.TaxCountry;
        request.ConsumerRightsRegime = quote.ConsumerRightsRegime;
        request.VatRateBasisPoints = quote.VatRateBasisPoints;
        request.GrossEurCents = quote.GrossEurCents;
        request.VatEurCents = quote.VatEurCents;
        request.OrderDetailsJson = OrderDetails();
        return request;
    }

    private static string OrderDetails(
        string marketplaceHash = ExpertMarketplaceAgreementHash,
        string creatorHash = CreatorMarketplaceAgreementHash) =>
        JsonConvert.SerializeObject(new
        {
            acceptedAgreement = new { hash = marketplaceHash },
            creatorAgreementHash = creatorHash
        });

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
        yield return Copy(original, consumerRightsRegime: "US");
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
        string withdrawalHash = null,
        string consumerRightsRegime = null) => new()
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
        ConsumerRightsRegime =
            consumerRightsRegime ?? source.ConsumerRightsRegime,
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
