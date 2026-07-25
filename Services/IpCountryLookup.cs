using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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

    public IpCountryLookup(HttpClient httpClient, ILogger<IpCountryLookup> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> GetCountry(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || IPAddress.IsLoopback(address))
            return null;

        try
        {
            using var response = await _httpClient.GetAsync($"{Uri.EscapeDataString(address.ToString())}/country/");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IP country lookup failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            var country = (await response.Content.ReadAsStringAsync()).Trim().ToUpperInvariant();
            return country.Length == 2 ? country : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IP country lookup failed");
            return null;
        }
    }
}
