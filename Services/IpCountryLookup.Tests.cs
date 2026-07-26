using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class IpCountryLookupTests
{
    [Test]
    public async Task GetCountry_ReturnsNormalizedCountry()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("de\n")
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ip.test/") };
        var lookup = CreateLookup(client);

        var country = await lookup.GetCountry("8.8.8.8");

        Assert.That(country, Is.EqualTo("DE"));
        Assert.That(handler.RequestUri, Is.EqualTo(new Uri("https://ip.test/8.8.8.8/country/")));
    }

    [TestCase(null)]
    [TestCase("not-an-ip")]
    [TestCase("127.0.0.1")]
    public async Task GetCountry_InvalidOrLoopbackIp_FailsClosed(string ip)
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var lookup = CreateLookup(new HttpClient(handler) { BaseAddress = new Uri("https://ip.test/") });

        Assert.That(await lookup.GetCountry(ip), Is.Null);
        Assert.That(handler.RequestUri, Is.Null);
    }

    [Test]
    public async Task GetCountry_PrimaryRateLimited_UsesFallbackForIpv6()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ip":"2003:e9::1","country":"de"}""")
            });
        var lookup = CreateLookup(new HttpClient(handler) { BaseAddress = new Uri("https://ip.test/") });

        var country = await lookup.GetCountry("2003:e9::1");

        Assert.That(country, Is.EqualTo("DE"));
        Assert.That(handler.RequestUris, Is.EqualTo(new[]
        {
            new Uri("https://ip.test/2003%3Ae9%3A%3A1/country/"),
            new Uri("https://fallback.test/2003%3Ae9%3A%3A1")
        }));
    }

    private static IpCountryLookup CreateLookup(HttpClient client)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["IP_COUNTRY:FALLBACK_BASE_URL"] = "https://fallback.test/"
            })
            .Build();
        return new IpCountryLookup(client, NullLogger<IpCountryLookup>.Instance, configuration);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public Uri RequestUri { get; private set; }
        public List<Uri> RequestUris { get; } = new();

        public StubHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestUris.Add(request.RequestUri);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
