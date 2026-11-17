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
/// <param name="PlanId">PayPal plan (P-...) with a one cycle TRIAL followed by an infinite REGULAR cycle of the plan's period</param>
/// <param name="CustomId">Internal reference, at most 127 characters</param>
/// <param name="Price">Gross price (discounted net price plus the buyer's VAT, see SubscriptionPricing.GrossCents) of the first cycle
/// (the plan's TRIAL cycle), sent as plan override; PayPal charges it as is</param>
/// <param name="RecurringPrice">Price of every following cycle (the REGULAR cycle); equals <paramref name="Price"/> unless a
/// "first payment only" code applies. Null means the same as <paramref name="Price"/>.</param>
public record PayPalSubscriptionRequest(string PlanId, string CustomId, decimal Price, string Currency,
    string ReturnUrl, string CancelUrl, string Email, decimal? RecurringPrice = null);

/// <summary>
/// A billing plan to create: a one cycle TRIAL then an infinite REGULAR cycle, both of <paramref name="Weeks"/> weeks
/// at the list price. The trial cycle exists so a subscription can price its first cycle differently.
/// </summary>
public record PayPalPlanRequest(string CatalogProductId, string Name, string Description, int Weeks, decimal Price, string Currency);

/// <summary>
/// PayPal Subscriptions v1 (the checkout SDK has no subscriptions API)
/// </summary>
public interface IPayPalSubscriptionApi
{
    /// <summary>Creates a catalog product (type SERVICE, category SOFTWARE) and returns its id</summary>
    Task<string> CreateCatalogProduct(string name, string description);
    /// <summary>Creates an active billing plan and returns its id</summary>
    Task<string> CreatePlan(PayPalPlanRequest request);
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

    public PayPalSubscriptionApi(PayPalHttpClient client)
    {
        this.client = client;
    }

    public async Task<string> CreateCatalogProduct(string name, string description) =>
        (await Send<PayPalResource>("/v1/catalogs/products", HttpMethod.Post, CatalogProductBody(name, description)))?.Id
        ?? throw new ApiException("PayPal did not return a catalog product id.");

    public async Task<string> CreatePlan(PayPalPlanRequest request) =>
        (await Send<PayPalResource>("/v1/billing/plans", HttpMethod.Post, PlanBody(request)))?.Id
        ?? throw new ApiException("PayPal did not return a plan id.");

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

    internal static JObject CatalogProductBody(string name, string description) => new()
    {
        ["name"] = Truncate(name, 127),
        ["description"] = Truncate(description, 256),
        ["type"] = "SERVICE",
        ["category"] = "SOFTWARE"
    };

    /// <summary>
    /// Sequence 1 is a one cycle TRIAL, sequence 2 the infinite REGULAR cycle; both at the list price.
    /// </summary>
    internal static JObject PlanBody(PayPalPlanRequest request) => new()
    {
        ["product_id"] = request.CatalogProductId,
        ["name"] = Truncate(request.Name, 127),
        ["description"] = Truncate(request.Description, 127),
        ["status"] = "ACTIVE",
        ["billing_cycles"] = new JArray(
            PlanCycle(request, "TRIAL", 1, 1),
            PlanCycle(request, "REGULAR", 2, 0)),
        ["payment_preferences"] = new JObject
        {
            ["auto_bill_outstanding"] = true,
            ["setup_fee"] = new JObject { ["value"] = "0", ["currency_code"] = request.Currency },
            ["setup_fee_failure_action"] = "CANCEL",
            ["payment_failure_threshold"] = 3
        }
    };

    private static JObject PlanCycle(PayPalPlanRequest request, string tenure, int sequence, int totalCycles) => new()
    {
        ["frequency"] = new JObject { ["interval_unit"] = "WEEK", ["interval_count"] = request.Weeks },
        ["tenure_type"] = tenure,
        ["sequence"] = sequence,
        ["total_cycles"] = totalCycles,
        ["pricing_scheme"] = PricingScheme(request.Price, request.Currency)
    };

    private static JObject PricingScheme(decimal price, string currency) => new()
    {
        ["fixed_price"] = new JObject { ["value"] = Money(price), ["currency_code"] = currency }
    };

    private static string Truncate(string text, int max) => text == null || text.Length <= max ? text : text[..max];

    /// <summary>
    /// The plan override prices the two cycles of the plan (sequence 1 TRIAL = first payment, sequence 2 REGULAR = renewals);
    /// the cycle structure itself must match the plan.
    /// </summary>
    internal static JObject CreateBody(PayPalSubscriptionRequest request)
    {
        var body = new JObject
        {
            ["plan_id"] = request.PlanId,
            ["custom_id"] = request.CustomId,
            ["plan"] = new JObject
            {
                ["billing_cycles"] = new JArray(
                    new JObject { ["sequence"] = 1, ["pricing_scheme"] = PricingScheme(request.Price, request.Currency) },
                    new JObject { ["sequence"] = 2, ["pricing_scheme"] = PricingScheme(request.RecurringPrice ?? request.Price, request.Currency) })
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
