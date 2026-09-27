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
using Moq;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class LemonSqueezySubscriptionTests
{
    private WebApplication app;
    private LemonSqueezyService provider;
    private VariantCacheService cacheService;
    private string method, body;
    private int status, price, priceVariant;
    private bool hasFreeTrial;
    private string interval;
    private int? intervalCount;
    private string currency;
    private TopUpProduct plan;

    [SetUp]
    public async Task Setup()
    {
        status = 200;
        price = 2969;
        priceVariant = 2118396;
        hasFreeTrial = false;
        interval = "week";
        intervalCount = 4;
        currency = "EUR";
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
                "/v1/variants/2118396" => new { product_id = 12, is_subscription = true, price, interval, interval_count = intervalCount, has_free_trial = hasFreeTrial },
                "/v1/products/12" => new { store_id = 34, status = "published" },
                "/v1/stores/34" => new { currency },
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
        cacheService = new VariantCacheService(NullLogger<VariantCacheService>.Instance);
        provider = new LemonSqueezyService(config, NullLogger<LemonSqueezyService>.Instance, null, cacheService);
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
    public async Task PlanChangeValidationStillThrowsOnFreeTrial()
    {
        await provider.ValidateSubscriptionVariant(plan, 2118396);
        hasFreeTrial = true;
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateSubscriptionVariant(plan, 2118396));
    }

    [Test]
    public async Task CheckoutValidationIgnoresListPriceMismatch()
    {
        price = 3369;
        Assert.DoesNotThrowAsync(() => provider.ValidateCheckoutVariant(plan, 2118396));
    }

    [Test]
    public async Task CheckoutValidationIgnoresFreeTrial()
    {
        hasFreeTrial = true;
        Assert.DoesNotThrowAsync(() => provider.ValidateCheckoutVariant(plan, 2118396));
    }

    [Test]
    public async Task CheckoutValidationThrowsOnIntervalMismatch()
    {
        // plan.OwnershipSeconds (2419200s = 4 weeks) expects "week_4"; the fake variant now reports monthly billing.
        interval = "month";
        intervalCount = 1;
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateCheckoutVariant(plan, 2118396));
    }

    [Test]
    public async Task CheckoutValidationThrowsOnCurrencyMismatch()
    {
        currency = "USD";
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateCheckoutVariant(plan, 2118396));
    }

    [Test]
    public async Task CheckoutValidationThrowsApiExceptionNotParseErrorWhenIntervalIsNull()
    {
        // Non-subscription variants report interval/interval_count as JSON null.
        interval = null;
        intervalCount = null;
        Assert.ThrowsAsync<ApiException>(() => provider.ValidateCheckoutVariant(plan, 2118396));
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

    [Test]
    public async Task SelectCheckoutVariant_UnmappedProduct_NeverReturnsReservedVariant()
    {
        // 2118396 is dedicated to l_premium-slots-4 (see Setup); an unrelated product must not get it
        // even though it is the closest price match, or its list price would be disturbed on LemonSqueezy.
        cacheService.AddVariantInfo("week_4", new VariantInfo { VariantId = "2118396", Price = 1299, HasFreeTrial = false });
        cacheService.AddVariantInfo("week_4", new VariantInfo { VariantId = "9999999", Price = 1299, HasFreeTrial = false });
        var unmapped = new TopUpProduct { Slug = "l_bazaarpro", Price = 12.99m, CurrencyCode = "eur", OwnershipSeconds = 2430000 };

        var (variantId, trialEnabled) = await provider.SelectCheckoutVariantAsync(unmapped, eurPrice: 12.99m, enableTrial: false);

        Assert.That(variantId, Is.Not.EqualTo("2118396"));
        Assert.That(variantId, Is.EqualTo("9999999"));
        Assert.That(trialEnabled, Is.False);
    }

    [Test]
    public async Task SelectCheckoutVariant_MappedProductAtPlanPrice_UsesDedicatedVariantAndDisablesTrial()
    {
        var (variantId, trialEnabled) = await provider.SelectCheckoutVariantAsync(plan, eurPrice: plan.Price, enableTrial: true);

        Assert.That(variantId, Is.EqualTo("2118396"));
        Assert.That(trialEnabled, Is.False);
    }

    [Test]
    public async Task SelectCheckoutVariant_MappedProductAtDiscountedPrice_AvoidsDedicatedVariant()
    {
        // A creator code / custom TopUpAmount can make the charged price differ from plan.Price;
        // the dedicated variant must be skipped so custom_price never overwrites its list price.
        cacheService.AddVariantInfo("week_4", new VariantInfo { VariantId = "2118396", Price = 2969, HasFreeTrial = false });
        cacheService.AddVariantInfo("week_4", new VariantInfo { VariantId = "8888888", Price = 2500, HasFreeTrial = false });

        var discountedPrice = 25.00m; // below plan.Price (29.69)
        var (variantId, trialEnabled) = await provider.SelectCheckoutVariantAsync(plan, eurPrice: discountedPrice, enableTrial: false);

        Assert.That(variantId, Is.Not.EqualTo("2118396"));
        Assert.That(variantId, Is.EqualTo("8888888"));
    }

    [Test]
    public async Task SelectCheckoutVariant_FallbackToReservedVariant_LogsWarningButDoesNotThrow()
    {
        var mockLogger = new Mock<ILogger<LemonSqueezyService>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LEMONSQUEEZY:API_BASE_URL"] = app.Urls.Single(), ["LEMONSQUEEZY:API_KEY"] = "test-key",
            ["LEMONSQUEEZY:STORE_ID"] = "34",
            ["LEMONSQUEEZY:SUBSCRIPTION_VARIANTS"] = "{\"l_premium-slots-4\":2118396}",
            // Misconfigured on purpose: the legacy fallback variant points at a dedicated variant.
            ["LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID"] = "2118396"
        }).Build();
        var fallbackProvider = new LemonSqueezyService(config, mockLogger.Object, null,
            new VariantCacheService(NullLogger<VariantCacheService>.Instance));
        // Empty cache for week_4 forces GetBestVariant to fail and GetVariantId to be used instead.
        var unmapped = new TopUpProduct { Slug = "l_bazaarpro", Price = 12.99m, CurrencyCode = "eur", OwnershipSeconds = 2419200 };

        (string VariantId, bool EnableTrial) result = default;
        Assert.DoesNotThrowAsync(async () => result = await fallbackProvider.SelectCheckoutVariantAsync(unmapped, eurPrice: 12.99m, enableTrial: false));

        Assert.That(result.VariantId, Is.EqualTo("2118396"));
        mockLogger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, t) => state.ToString().Contains("reserved")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);
    }
}
