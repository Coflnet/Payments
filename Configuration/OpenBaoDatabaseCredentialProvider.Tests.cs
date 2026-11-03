using System;
using System.Text.Json;
using System.IO;
using NUnit.Framework;

namespace Coflnet.Security.OpenBao;

[TestFixture]
public sealed class OpenBaoDatabaseCredentialProviderTests
{
    [TestCase(1800, 5)]
    [TestCase(10, 5)]
    [TestCase(5, 5)]
    [TestCase(2, 2)]
    public void ProviderPollIntervalCapsTheStalePasswordWindow(int configuredSeconds, int expectedSeconds)
    {
        Assert.That(
            OpenBaoDatabaseCredentialProvider.ProviderPollInterval(TimeSpan.FromSeconds(configuredSeconds)),
            Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    [TestCase(86400, 86400)]
    [TestCase(5, 5)]
    [TestCase(0, 1)]
    public void CredentialRefreshDelayUsesStaticRoleTtl(int ttlSeconds, int expectedSeconds)
    {
        using var document = JsonDocument.Parse($"{{\"ttl\":{ttlSeconds}}}");

        Assert.That(
            OpenBaoDatabaseCredentialProvider.CredentialRefreshDelay(
                document.RootElement,
                TimeSpan.FromMinutes(30)),
            Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    [Test]
    public void CredentialRefreshDelayFallsBackWhenLegacyResponseOmitsTtl()
    {
        using var document = JsonDocument.Parse("{}");

        Assert.That(
            OpenBaoDatabaseCredentialProvider.CredentialRefreshDelay(
                document.RootElement,
                TimeSpan.FromMinutes(30)),
            Is.EqualTo(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public void MtlsValidationIsInertByDefault()
    {
        var options = new OpenBaoDatabaseOptions
        {
            Address = "https://openbao.example",
            Role = "payment-test",
            TokenPath = Path.GetTempFileName()
        };
        try
        {
            Assert.DoesNotThrow(options.Validate);
        }
        finally
        {
            File.Delete(options.TokenPath);
        }
    }

    [Test]
    public void MtlsValidationRequiresAllCertificateFiles()
    {
        var token = Path.GetTempFileName();
        try
        {
            var options = new OpenBaoDatabaseOptions
            {
                Address = "https://openbao.example",
                Role = "payment-test",
                TokenPath = token,
                MtlsEnabled = true
            };
            Assert.That(
                () => options.Validate(),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("OPENBAO__DB__MTLS__CLIENT_CERT_PATH"));
        }
        finally
        {
            File.Delete(token);
        }
    }

    private const string BaseConnection = "Host=db;Port=26257;Database=pay;Username=svc;Password=old;Trust Server Certificate=true";

    private static OpenBaoDatabaseOptions MtlsOptions() => new()
    {
        MtlsEnabled = true,
        ClientCertificatePath = "/certs/tls.crt",
        ClientKeyPath = "/certs/tls.key",
        RootCertificatePath = "/certs/ca.crt"
    };

    [Test]
    public void MtlsPathUsesNpgsqlChainFilesWithVerifyFull()
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(BaseConnection, MtlsOptions());
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);

        Assert.That(builder.SslMode, Is.EqualTo(Npgsql.SslMode.VerifyFull));
        Assert.That(builder.SslCertificate, Is.EqualTo("/certs/tls.crt"));
        Assert.That(builder.SslKey, Is.EqualTo("/certs/tls.key"));
        Assert.That(builder.RootCertificate, Is.EqualTo("/certs/ca.crt"));
        Assert.That(builder.TrustServerCertificate, Is.False);
        Assert.That(connection, Does.Not.Contain("Trust Server Certificate"));
        Assert.That(builder.Password, Is.Null);
    }

    [Test]
    public void MtlsPathCapsConnectionLifetimeAndTimeout()
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(
            BaseConnection + ";Connection Lifetime=3600;Timeout=60", MtlsOptions());
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);

        Assert.That(builder.ConnectionLifetime, Is.EqualTo(300));
        Assert.That(builder.Timeout, Is.EqualTo(10));
    }

    [Test]
    public void MtlsPathKeepsSmallerConfiguredLifetime()
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(
            BaseConnection + ";Connection Lifetime=60;Timeout=5", MtlsOptions());
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);

        Assert.That(builder.ConnectionLifetime, Is.EqualTo(60));
        Assert.That(builder.Timeout, Is.EqualTo(5));
    }

    [Test]
    public void NonMtlsPathOnlyStripsThePassword()
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(
            BaseConnection, new OpenBaoDatabaseOptions());
        var expected = new Npgsql.NpgsqlConnectionStringBuilder(BaseConnection) { Password = null }.ConnectionString;

        Assert.That(connection, Is.EqualTo(expected));
        Assert.That(new Npgsql.NpgsqlConnectionStringBuilder(connection).TrustServerCertificate, Is.True);
    }

    [TestCase("TrustServerCertificate=true")]
    [TestCase("Trust Server Certificate=true")]
    public void MtlsPathNeutralisesTrustServerCertificateInBase(string setting)
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(
            "Host=db;Username=svc;" + setting, MtlsOptions());

        Assert.That(new Npgsql.NpgsqlConnectionStringBuilder(connection).TrustServerCertificate, Is.False);
        Assert.That(connection, Does.Not.Contain("Trust Server Certificate"));
    }

    [Test]
    public void MtlsPathOverridesStaleSslSettingsInBase()
    {
        var connection = OpenBaoDatabaseCredentialProvider.BuildConnectionString(
            "Host=db;Username=svc;SSL Certificate=/old/c.crt;SSL Key=/old/c.key;Root Certificate=/old/ca.crt;SSL Password=stale",
            MtlsOptions());
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection);

        Assert.That(builder.SslCertificate, Is.EqualTo("/certs/tls.crt"));
        Assert.That(builder.SslKey, Is.EqualTo("/certs/tls.key"));
        Assert.That(builder.RootCertificate, Is.EqualTo("/certs/ca.crt"));
        Assert.That(builder.SslPassword, Is.Null);
    }
}
