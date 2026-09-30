using System.Threading.Tasks;

namespace Coflnet.Payments.Services;

/// <summary>
/// Billing location of a Lemon Squeezy customer.
/// </summary>
/// <param name="Country">Upper-cased ISO 3166-1 alpha-2 country</param>
/// <param name="Region">Region/state, truncated to fit PaymentRecord.State</param>
public record LemonSqueezyCustomerLocation(string Country, string Region);

/// <summary>
/// Resolves buyer locations from the Lemon Squeezy API. The order webhook carries no country,
/// so it is fetched from the customer object (attributes.country).
/// </summary>
public interface ILemonSqueezyCustomerLookup
{
    /// <summary>
    /// GET /v1/customers/{id}. Returns null when the customer has no country.
    /// </summary>
    Task<LemonSqueezyCustomerLocation> GetCustomerLocationAsync(string customerId);

    /// <summary>
    /// GET /v1/orders/{id} to find the customer, then the customer location.
    /// </summary>
    Task<LemonSqueezyCustomerLocation> GetOrderCustomerLocationAsync(string orderId);
}
