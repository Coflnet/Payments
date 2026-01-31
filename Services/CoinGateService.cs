using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.CoinGate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Service for interacting with CoinGate cryptocurrency payment API
/// </summary>
public class CoinGateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CoinGateService> _logger;
    private readonly IConfiguration _config;
    private readonly string _apiToken;
    private readonly string _baseUrl;
    private readonly bool _isSandbox;

    /// <summary>
    /// Initializes a new instance of the CoinGateService
    /// </summary>
    public CoinGateService(IConfiguration config, ILogger<CoinGateService> logger)
    {
        _config = config;
        _logger = logger;
        _apiToken = config["COINGATE:API_TOKEN"];
        _isSandbox = bool.TryParse(config["COINGATE:IS_SANDBOX"], out var sandbox) && sandbox;
        _baseUrl = _isSandbox 
            ? "https://api-sandbox.coingate.com/v2" 
            : "https://api.coingate.com/v2";

        if (!string.IsNullOrEmpty(_apiToken))
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _apiToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            _logger.LogInformation("CoinGate service initialized in {Mode} mode", _isSandbox ? "sandbox" : "live");
        }
        else
        {
            _logger.LogWarning("CoinGate API token not configured - CoinGate payments will be disabled");
        }
    }

    /// <summary>
    /// Creates a new order in CoinGate
    /// </summary>
    /// <param name="user">The user making the purchase</param>
    /// <param name="product">The product being purchased</param>
    /// <param name="eurPrice">The price in EUR</param>
    /// <param name="coinAmount">The amount of coins to credit</param>
    /// <param name="options">Additional topup options</param>
    /// <returns>TopUpIdResponse with payment URL</returns>
    public async Task<TopUpIdResponse> CreateOrder(
        User user, 
        TopUpProduct product, 
        decimal eurPrice, 
        decimal coinAmount,
        TopUpOptions options = null)
    {
        if (!IsConfigured)
        {
            throw new ApiException("CoinGate payments are not configured");
        }

        var callbackBaseUrl = _config["COINGATE:CALLBACK_BASE_URL"] ?? _config["DEFAULT:CALLBACK_BASE_URL"];
        var orderId = $"CG-{user.ExternalId}-{product.Id}-{DateTime.UtcNow.Ticks}";
        
        // Generate a secure token for callback validation
        var callbackToken = GenerateCallbackToken(orderId, user.ExternalId, product.Id, coinAmount);

        var request = new CoinGateCreateOrderRequest
        {
            OrderId = orderId,
            PriceAmount = eurPrice,
            PriceCurrency = product.CurrencyCode ?? "EUR",
            ReceiveCurrency = _config["COINGATE:RECEIVE_CURRENCY"] ?? "EUR",
            Title = product.Title,
            Description = $"{product.Description} - {coinAmount:N0} coins for user {user.ExternalId}",
            CallbackUrl = $"{callbackBaseUrl}/Callback/coingate?userId={Uri.EscapeDataString(user.ExternalId)}&productId={product.Id}&coinAmount={coinAmount}",
            SuccessUrl = options?.SuccessUrl ?? _config["DEFAULT:SUCCESS_URL"],
            CancelUrl = options?.CancelUrl ?? _config["DEFAULT:CANCEL_URL"],
            Token = callbackToken,
            PurchaserEmail = options?.UserEmail
        };

        var jsonContent = JsonSerializer.Serialize(request);
        _logger.LogInformation("Creating CoinGate order: {OrderId} for user {UserId}, amount {Amount} {Currency}", 
            orderId, user.ExternalId, eurPrice, product.CurrencyCode);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/orders")
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(httpRequest);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CoinGate API error: {StatusCode} - {Response}", response.StatusCode, responseContent);
            throw new ApiException($"CoinGate payment creation failed: {responseContent}");
        }

        var orderResponse = JsonSerializer.Deserialize<CoinGateOrderResponse>(responseContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _logger.LogInformation("CoinGate order created: {CoinGateOrderId}, PaymentUrl: {PaymentUrl}", 
            orderResponse.Id, orderResponse.PaymentUrl);

        return new TopUpIdResponse
        {
            Id = orderResponse.Id.ToString(),
            DirectLink = orderResponse.PaymentUrl
        };
    }

    /// <summary>
    /// Retrieves an order from CoinGate by ID to verify its status
    /// </summary>
    /// <param name="orderId">The CoinGate order ID</param>
    /// <returns>The order details</returns>
    public async Task<CoinGateOrderResponse> GetOrder(long orderId)
    {
        if (!IsConfigured)
        {
            throw new ApiException("CoinGate payments are not configured");
        }

        _logger.LogInformation("Fetching CoinGate order: {OrderId}", orderId);

        var response = await _httpClient.GetAsync($"{_baseUrl}/orders/{orderId}");
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CoinGate API error fetching order {OrderId}: {StatusCode} - {Response}", 
                orderId, response.StatusCode, responseContent);
            throw new ApiException($"Failed to fetch CoinGate order: {responseContent}");
        }

        var order = JsonSerializer.Deserialize<CoinGateOrderResponse>(responseContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _logger.LogInformation("CoinGate order {OrderId} status: {Status}", orderId, order.Status);
        return order;
    }

    /// <summary>
    /// Verifies a callback by fetching the order from CoinGate API and comparing data
    /// </summary>
    /// <param name="callback">The callback data received</param>
    /// <param name="expectedUserId">Expected user ID from callback URL parameters</param>
    /// <param name="expectedProductId">Expected product ID from callback URL parameters</param>
    /// <param name="expectedCoinAmount">Expected coin amount from callback URL parameters</param>
    /// <returns>True if the callback is valid</returns>
    public async Task<bool> VerifyCallback(CoinGateCallback callback, string expectedUserId, int expectedProductId, decimal expectedCoinAmount)
    {
        try
        {
            // Fetch the order directly from CoinGate API to verify
            var order = await GetOrder(callback.Id);

            // Verify the order matches the callback data
            if (order.Status != callback.Status)
            {
                _logger.LogWarning("CoinGate callback verification failed: Status mismatch. API: {ApiStatus}, Callback: {CallbackStatus}",
                    order.Status, callback.Status);
                return false;
            }

            if (order.PriceAmount != callback.PriceAmount)
            {
                _logger.LogWarning("CoinGate callback verification failed: Price mismatch. API: {ApiPrice}, Callback: {CallbackPrice}",
                    order.PriceAmount, callback.PriceAmount);
                return false;
            }

            // Verify the order_id contains expected values (our custom order ID format)
            var expectedToken = GenerateCallbackToken(callback.OrderId, expectedUserId, expectedProductId, expectedCoinAmount);
            if (callback.Token != expectedToken)
            {
                _logger.LogWarning("CoinGate callback verification failed: Token mismatch for order {OrderId}", callback.OrderId);
                return false;
            }

            _logger.LogInformation("CoinGate callback verified successfully for order {OrderId}", callback.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoinGate callback verification failed for order {OrderId}", callback.Id);
            return false;
        }
    }

    /// <summary>
    /// Generates a secure callback token for validation
    /// </summary>
    private string GenerateCallbackToken(string orderId, string userId, int productId, decimal coinAmount)
    {
        var secret = _config["COINGATE:CALLBACK_SECRET"] ?? _apiToken;
        var data = $"{orderId}:{userId}:{productId}:{coinAmount}:{secret}";
        
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hashBytes).Substring(0, 32); // Truncate for cleaner token
    }

    /// <summary>
    /// Checks if the service is configured and ready to use
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiToken);
}
