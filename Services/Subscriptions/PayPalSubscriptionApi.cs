using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models.PayPal;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PayPalCheckoutSdk.Core;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// A new PayPal subscription priced by this service
/// </summary>
/// <param name="PlanId">Configured PayPal plan (P-...) with one REGULAR billing cycle of the plan's period</param>
/// <param name="CustomId">Internal reference, at most 127 characters</param>
/// <param name="Price">VAT-inclusive (and discounted) price per cycle, sent as plan override</param>
public record PayPalSubscriptionRequest(string PlanId, string CustomId, decimal Price, string Currency,
    string ReturnUrl, string CancelUrl, string Email);

/// <summary>
/// PayPal Subscriptions v1 (the checkout SDK has no subscriptions API)
/// </summary>
public interface IPayPalSubscriptionApi
{
    /// <summary>The configured plan id for a product slug (PAYPAL:SUBSCRIPTION_PLANS:{slug}), null if none</summary>
    string PlanIdFor(string productSlug);
    Task<PayPalSubscription> Create(PayPalSubscriptionRequest request);
    Task<PayPalSubscription> Get(string subscriptionId);
    /// <summary>Cancels immediately and finally (PayPal has no cancel at period end)</summary>
    Task Cancel(string subscriptionId, string reason);
    Task<IReadOnlyList<PayPalTransaction>> Transactions(string subscriptionId, DateTime start, DateTime end);
    Task<PayPalSale> GetSale(string saleId);
    /// <summary>Refunds a sale, fully when <paramref name="amount"/> is null</summary>
    Task<PayPalSale> RefundSale(string saleId, decimal? amount, string currency);
}

/// <summary>
/// Raw REST calls through the authenticated <see cref="PayPalHttpClient"/>
/// </summary>
public class PayPalSubscriptionApi : IPayPalSubscriptionApi
{
    private readonly PayPalHttpClient client;
    private readonly IConfiguration config;

    public PayPalSubscriptionApi(PayPalHttpClient client, IConfiguration config)
    {
        this.client = client;
        this.config = config;
    }

    public string PlanIdFor(string productSlug) =>
        string.IsNullOrWhiteSpace(productSlug) ? null : config?[$"PAYPAL:SUBSCRIPTION_PLANS:{productSlug}"];

    public Task<PayPalSubscription> Create(PayPalSubscriptionRequest request) =>
        Send<PayPalSubscription>("/v1/billing/subscriptions", HttpMethod.Post, CreateBody(request));

    public Task<PayPalSubscription> Get(string subscriptionId) =>
        Send<PayPalSubscription>(SubscriptionPath(subscriptionId), HttpMethod.Get);

    public Task Cancel(string subscriptionId, string reason) =>
        Send<PayPalSubscription>(SubscriptionPath(subscriptionId) + "/cancel", HttpMethod.Post,
            new JObject { ["reason"] = reason.Length > 128 ? reason[..128] : reason });

    public async Task<IReadOnlyList<PayPalTransaction>> Transactions(string subscriptionId, DateTime start, DateTime end)
    {
        var path = $"{SubscriptionPath(subscriptionId)}/transactions?start_time={Time(start)}&end_time={Time(end)}";
        var list = await Send<PayPalTransactionList>(path, HttpMethod.Get);
        return list?.Transactions ?? [];
    }

    public Task<PayPalSale> GetSale(string saleId) =>
        Send<PayPalSale>("/v1/payments/sale/" + Escape(saleId), HttpMethod.Get);

    public Task<PayPalSale> RefundSale(string saleId, decimal? amount, string currency)
    {
        var body = amount == null ? new JObject() : new JObject
        {
            ["amount"] = new JObject { ["total"] = Money(amount.Value), ["currency"] = currency }
        };
        return Send<PayPalSale>($"/v1/payments/sale/{Escape(saleId)}/refund", HttpMethod.Post, body);
    }

    /// <summary>
    /// The plan override changes only the price; the billing cycle structure must match the plan.
    /// </summary>
    internal static JObject CreateBody(PayPalSubscriptionRequest request)
    {
        var body = new JObject
        {
            ["plan_id"] = request.PlanId,
            ["custom_id"] = request.CustomId,
            ["plan"] = new JObject
            {
                ["billing_cycles"] = new JArray(new JObject
                {
                    ["sequence"] = 1,
                    ["pricing_scheme"] = new JObject
                    {
                        ["fixed_price"] = new JObject { ["value"] = Money(request.Price), ["currency_code"] = request.Currency }
                    }
                })
            },
            ["application_context"] = ApplicationContext(request.ReturnUrl, request.CancelUrl)
        };
        if (!string.IsNullOrWhiteSpace(request.Email))
            body["subscriber"] = new JObject { ["email_address"] = request.Email };
        return body;
    }

    /// <summary>
    /// application_context is deprecated in favour of payment_source but still supported; kept here so it is easy to swap.
    /// </summary>
    private static JObject ApplicationContext(string returnUrl, string cancelUrl) => new()
    {
        ["user_action"] = "SUBSCRIBE_NOW",
        ["shipping_preference"] = "NO_SHIPPING",
        ["return_url"] = returnUrl,
        ["cancel_url"] = cancelUrl
    };

    private async Task<T> Send<T>(string path, HttpMethod method, JObject body = null)
    {
        using var request = new PayPalHttp.HttpRequest(path, method, typeof(T));
        if (body != null)
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        var response = await client.Execute(request);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? default : response.Result<T>();
    }

    private static string SubscriptionPath(string subscriptionId) => "/v1/billing/subscriptions/" + Escape(subscriptionId);

    private static string Escape(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ApiException("Invalid PayPal id");
        return id;
    }

    private static string Time(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    internal static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
