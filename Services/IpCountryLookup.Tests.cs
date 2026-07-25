using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
        var lookup = new IpCountryLookup(client, NullLogger<IpCountryLookup>.Instance);

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
        var lookup = new IpCountryLookup(
            new HttpClient(handler) { BaseAddress = new Uri("https://ip.test/") },
            NullLogger<IpCountryLookup>.Instance);

        Assert.That(await lookup.GetCountry(ip), Is.Null);
        Assert.That(handler.RequestUri, Is.Null);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public Uri RequestUri { get; private set; }

        public StubHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(_response);
        }
    }
}
