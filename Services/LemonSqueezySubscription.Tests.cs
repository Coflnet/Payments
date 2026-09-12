using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class LemonSqueezySubscriptionTests
{
    private WebApplication app;
    private LemonSqueezyService provider;
    private string method, body;
    private int status, price, priceVariant;
    private TopUpProduct plan;

    [SetUp]
    public async Task Setup()
    {
        status = 200;
        price = 2969;
        priceVariant = 2118396;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.Run(async context =>
        {
            method = context.Request.Method;
            body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            Assert.That(context.Request.Headers.Authorization.ToString(), Is.EqualTo("Bearer test-key"));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/vnd.api+json";
            object attributes = context.Request.Path.Value switch
            {
                "/v1/variants/2118396" => new { product_id = 12, is_subscription = true, price, interval = "week", interval_count = 4, has_free_trial = false },
                "/v1/products/12" => new { store_id = 34, status = "published" },
                "/v1/stores/34" => new { currency = "EUR" },
                "/v1/prices/56" => new { unit_price = price, variant_id = priceVariant },
                _ => new { variant_id = 2118396, status = "active", renews_at = "2026-10-01T00:00:00Z",
                    first_subscription_item = new { price_id = 56, quantity = 1 }, payment_processor = "paypal", urls = new { customer_portal_update_subscription = "https://test.lemonsqueezy.com/billing/update" } }
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { data = new { type = "subscriptions", id = "42", attributes } }));
        });
        await app.StartAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LEMONSQUEEZY:API_BASE_URL"] = app.Urls.Single(), ["LEMONSQUEEZY:API_KEY"] = "test-key",
            ["LEMONSQUEEZY:STORE_ID"] = "34",
            ["LEMONSQUEEZY:SUBSCRIPTION_VARIANTS"] = "{\"l_premium-slots-4\":2118396}"
        }).Build();
        provider = new LemonSqueezyService(config, NullLogger<LemonSqueezyService>.Instance, null,
            new VariantCacheService(NullLogger<VariantCacheService>.Instance));
        plan = new TopUpProduct { Slug = "l_premium-slots-4", Price = 29.69m, CurrencyCode = "eur", OwnershipSeconds = 2419200 };
    }

    [TearDown]
    public async Task Cleanup() => await app.DisposeAsync();

    [TestCase(true)]
    [TestCase(false)]
    public async Task PlanChangeUsesJsonApiAndExplicitProrationPolicy(bool upgrade)
    {
        var response = await provider.ChangeSubscription("42", 2118396, upgrade);
        Assert.That(method, Is.EqualTo("PATCH"));
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.That(data.GetProperty("id").GetString(), Is.EqualTo("42"));
        var attrs = data.GetProperty("attributes");
        Assert.That(attrs.GetProperty("variant_id").GetInt64(), Is.EqualTo(2118396));
        Assert.That(attrs.GetProperty("invoice_immediately").GetBoolean(), Is.EqualTo(upgrade));
        Assert.That(attrs.GetProperty("disable_prorations").GetBoolean(), Is.EqualTo(!upgrade));
        Assert.That(attrs.TryGetProperty("billing_anchor", out _), Is.False);
        Assert.That(response.Attributes.Urls.CustomerPortalUpdateSubscription, Does.EndWith("/billing/update"));
    }

    [Test]
    public async Task ExactVariantMappingRequiresMatchingPublishedStorePriceAndInterval()
    {
        Assert.That(provider.SubscriptionVariants[plan.Slug], Is.EqualTo(2118396));
        await provider.ValidateSubscriptionVariant(plan, 2118396);
        price = 3369;
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateSubscriptionVariant(plan, 2118396));
    }

    [Test]
    public async Task CancellationAndReactivationUseProviderResponsesAndSurfaceFailure()
    {
        await provider.CancelSubscription("42");
        Assert.That(method, Is.EqualTo("DELETE"));
        await provider.ResumeSubscription("42");
        Assert.That(method, Is.EqualTo("PATCH"));
        using var json = JsonDocument.Parse(body);
        Assert.That(json.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("cancelled").GetBoolean(), Is.False);
        status = 503;
        var error = Assert.ThrowsAsync<ApiException>(() => provider.CancelSubscription("42"));
        Assert.That(error.Message, Does.Not.Contain("test-key"));
    }
    [Test]
    public async Task ProviderPriceMustChangeEvenWhenLegacyCheckoutUsedTheSameVariant()
    {
        var subscription = await provider.GetSubscription("42");
        Assert.That(await provider.ValidateSubscriptionPrice(subscription, plan), Is.True);
        priceVariant = 1278931;
        Assert.That(await provider.ValidateSubscriptionPrice(subscription, plan), Is.False);
        priceVariant = 2118396;
        price = 9969;
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateSubscriptionPrice(subscription, plan));
    }

}
