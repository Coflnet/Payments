using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

public interface IIpCountryLookup
{
    Task<string> GetCountry(string ip);
}

public class IpCountryLookup : IIpCountryLookup
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<IpCountryLookup> _logger;
    private readonly Uri _fallbackBaseAddress;

    public IpCountryLookup(
        HttpClient httpClient,
        ILogger<IpCountryLookup> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _fallbackBaseAddress = new Uri(
            (configuration["IP_COUNTRY:FALLBACK_BASE_URL"] ?? "https://api.country.is/").TrimEnd('/') + "/");
    }

    public async Task<string> GetCountry(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || IPAddress.IsLoopback(address))
            return null;

        var escapedAddress = Uri.EscapeDataString(address.ToString());
        var country = await GetCountry(
            new Uri(_httpClient.BaseAddress, $"{escapedAddress}/country/"),
            content => content.Trim());
        if (country != null)
            return country;

        return await GetCountry(
            new Uri(_fallbackBaseAddress, escapedAddress),
            content =>
            {
                using var json = JsonDocument.Parse(content);
                return json.RootElement.TryGetProperty("country", out var value)
                    ? value.GetString()
                    : null;
            });
    }

    private async Task<string> GetCountry(Uri uri, Func<string, string> parseCountry)
    {
        try
        {
            using var response = await _httpClient.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "IP country lookup at {Provider} failed with status {StatusCode}",
                    uri.Host,
                    response.StatusCode);
                return null;
            }

            var country = parseCountry(await response.Content.ReadAsStringAsync())?.ToUpperInvariant();
            if (country?.Length == 2)
                return country;

            _logger.LogWarning("IP country lookup at {Provider} returned an invalid response", uri.Host);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IP country lookup at {Provider} failed", uri.Host);
        }

        return null;
    }
}
