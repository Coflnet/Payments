using System;
using System.Text.Json;
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
}
