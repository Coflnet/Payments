using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace Coflnet.Payments.Services;

public class LemonSqueezyService
{
    private IConfiguration config;
    private ILogger<LemonSqueezyService> logger;
    private PaymentContext context;
    private VariantCacheService variantCache;

    /// <summary>
    /// Creates a new instance of LemonSqueezyService
    /// </summary>
    public LemonSqueezyService(IConfiguration config, ILogger<LemonSqueezyService> logger, PaymentContext context, VariantCacheService variantCache)
    {
        this.config = config;
        this.logger = logger;
        this.context = context;
        this.variantCache = variantCache;
    }

    /// <summary>
    /// Discover and cache product variants from LemonSqueezy API
    /// This should be called on startup to populate variant IDs automatically
    /// </summary>
    public async Task DiscoverVariantsAsync()
    {
        try
        {
            var storeId = config["LEMONSQUEEZY:STORE_ID"];
            var restclient = new RestClient("https://api.lemonsqueezy.com");

            logger.LogInformation("Starting variant discovery for store {StoreId}", storeId);

            // Fetch all products for the store
            var productsRequest = new RestRequest($"/v1/products?filter[store_id]={storeId}", Method.Get);
            productsRequest.AddHeader("Accept", "application/vnd.api+json");
            productsRequest.AddHeader("Content-Type", "application/vnd.api+json");
            productsRequest.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

            var productsResponse = await restclient.ExecuteAsync(productsRequest);
            if (!productsResponse.IsSuccessful)
            {
                logger.LogError("Failed to fetch products: {StatusCode} {Content}",
                    productsResponse.StatusCode, productsResponse.Content);
                return;
            }

            var products = System.Text.Json.JsonSerializer.Deserialize<ProductListResponse>(
                productsResponse.Content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (products?.Data == null)
            {
                logger.LogWarning("No products found in response");
                return;
            }

            logger.LogInformation("Found {Count} products", products.Data.Length);

            // For each product, fetch its variants
            foreach (var product in products.Data.Where(p => p.Attributes.Status == "published"))
            {
                var variantsRequest = new RestRequest($"/v1/products/{product.Id}/variants", Method.Get);
                variantsRequest.AddHeader("Accept", "application/vnd.api+json");
                variantsRequest.AddHeader("Content-Type", "application/vnd.api+json");
                variantsRequest.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

                var variantsResponse = await restclient.ExecuteAsync(variantsRequest);
                if (!variantsResponse.IsSuccessful)
                {
                    logger.LogWarning("Failed to fetch variants for product {ProductId}: {StatusCode}",
                        product.Id, variantsResponse.StatusCode);
                    continue;
                }

                var variants = System.Text.Json.JsonSerializer.Deserialize<VariantListResponse>(
                    variantsResponse.Content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (variants?.Data == null || variants.Data.Length == 0)
                    continue;

                // Process subscription variants - store full variant info for intelligent selection
                foreach (var variant in variants.Data.Where(v => v.Attributes.IsSubscription))
                {
                    var attrs = variant.Attributes;
                    var key = GetVariantCacheKey(attrs.Interval, attrs.IntervalCount);

                    // Create VariantInfo with all relevant details
                    var variantInfo = new VariantInfo
                    {
                        VariantId = variant.Id,
                        ProductName = product.Attributes.Name,
                        VariantName = attrs.Name,
                        Price = attrs.Price,
                        HasFreeTrial = attrs.HasFreeTrial,
                        Interval = attrs.Interval,
                        IntervalCount = attrs.IntervalCount,
                        IsSubscription = attrs.IsSubscription,
                        ProductId = attrs.ProductId
                    };

                    // Add to variant cache service
                    variantCache.AddVariantInfo(key, variantInfo);

                    // For backward compatibility, also update simple cache with first non-trial variant
                    if (!attrs.HasFreeTrial)
                    {
                        variantCache.AddSimpleVariant(key, variant.Id);
                    }

                    logger.LogInformation("Discovered variant: {ProductName} - {VariantName} ({Interval} x {Count}) Price: {Price} Trial: {HasTrial} = ID {VariantId}",
                        product.Attributes.Name, attrs.Name, attrs.Interval, attrs.IntervalCount, attrs.Price, attrs.HasFreeTrial, variant.Id);
                }

                // Process non-subscription variants (regular top-up)
                var regularVariant = variants.Data.FirstOrDefault(v => !v.Attributes.IsSubscription);
                if (regularVariant != null)
                {
                    variantCache.AddSimpleVariant("regular", regularVariant.Id);
                    logger.LogInformation("Discovered regular variant: {ProductName} = ID {VariantId}",
                        product.Attributes.Name, regularVariant.Id);
                }
            }

            var stats = variantCache.GetStats();
            logger.LogInformation("Variant discovery complete. Cached {SimpleCount} simple variants, {EnhancedCount} interval groups with {TotalVariants} total variants",
                stats.SimpleVariants, stats.IntervalGroups, stats.TotalVariants);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during variant discovery");
        }
    }

    /// <summary>
    /// Get variant ID from cache or configuration based on subscription duration
    /// </summary>
    /// <param name="ownershipSeconds">Subscription duration in seconds</param>
    /// <returns>Variant ID string</returns>
    public string GetVariantId(int ownershipSeconds)
    {
        // Try cache first
        if (ownershipSeconds == (int)TimeSpan.FromDays(30).TotalSeconds)
        {
            if (variantCache.TryGetSimpleVariant("week_4", out var monthlyId))
                return monthlyId;
        }
        else if (ownershipSeconds == (int)TimeSpan.FromDays(90).TotalSeconds)
        {
            if (variantCache.TryGetSimpleVariant("month_3", out var quarterlyId))
                return quarterlyId;
        }
        else if (ownershipSeconds == (int)TimeSpan.FromDays(365).TotalSeconds)
        {
            if (variantCache.TryGetSimpleVariant("year_1", out var yearlyId))
                return yearlyId;
        }

        // Fallback to configuration
        if (ownershipSeconds == (int)TimeSpan.FromDays(365).TotalSeconds)
            return config["LEMONSQUEEZY:YEAR_SUBSCRIPTION_VARIANT_ID"];
        else if (ownershipSeconds == (int)TimeSpan.FromDays(90).TotalSeconds)
            return config["LEMONSQUEEZY:QUARTER_SUBSCRIPTION_VARIANT_ID"];

        return config["LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID"];
    }

    private string GetVariantCacheKey(string interval, int intervalCount)
    {
        return $"{interval}_{intervalCount}";
    }

    /// <summary>
    /// Get the best variant for a subscription based on duration, trial preference, and price.
    /// Delegates to VariantCacheService for actual selection logic.
    /// </summary>
    public VariantInfo GetBestVariant(int ownershipSeconds, bool enableTrial, int? targetPrice = null)
    {
        return variantCache.GetBestVariant(ownershipSeconds, enableTrial, targetPrice);
    }

    /// <summary>
    /// Check if a user has already used a trial for a specific product
    /// </summary>
    /// <param name="userId">External user ID</param>
    /// <param name="productId">Product ID</param>
    /// <returns>True if user has already used trial for this product</returns>
    public async Task<bool> HasUserUsedTrialAsync(string userId, int productId)
    {
        var user = await context.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
        if (user == null)
            return false;

        return await context.TrialUsages
            .AnyAsync(t => t.UserId == user.Id && t.ProductId == productId);
    }

    /// <summary>
    /// Record that a user has used a trial for a product
    /// </summary>
    /// <param name="userId">External user ID</param>
    /// <param name="productId">Product ID</param>
    /// <param name="externalSubscriptionId">External subscription ID from LemonSqueezy</param>
    public async Task RecordTrialUsageAsync(string userId, int productId, string externalSubscriptionId)
    {
        var user = await context.Users.Where(u => u.ExternalId == userId).FirstOrDefaultAsync();
        if (user == null)
        {
            logger.LogWarning("Cannot record trial usage: user {UserId} not found", userId);
            return;
        }

        // Check if already recorded
        var existingUsage = await context.TrialUsages
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ProductId == productId);

        if (existingUsage != null)
        {
            logger.LogInformation("Trial usage already recorded for user {UserId} product {ProductId}", userId, productId);
            return;
        }

        var trialUsage = new TrialUsage
        {
            UserId = user.Id,
            ProductId = productId,
            TrialStartedAt = DateTime.UtcNow,
            ExternalSubscriptionId = externalSubscriptionId
        };

        context.TrialUsages.Add(trialUsage);
        await context.SaveChangesAsync();
        logger.LogInformation("Recorded trial usage for user {UserId} product {ProductId} subscription {SubscriptionId}",
            userId, productId, externalSubscriptionId);
    }

    /// <summary>
    /// Validates a discount code by looking it up in the LemonSqueezy API
    /// </summary>
    /// <param name="discountCode">The discount code to validate</param>
    /// <param name="isSubscription">Whether this is for a subscription purchase (null = don't filter)</param>
    /// <returns>Validated discount info or null if invalid/not found</returns>
    public async Task<ValidatedDiscount> ValidateDiscountCodeAsync(string discountCode, bool? isSubscription = null)
    {
        if (string.IsNullOrWhiteSpace(discountCode))
        {
            return null;
        }

        try
        {
            var storeId = config["LEMONSQUEEZY:STORE_ID"];
            var restclient = new RestClient("https://api.lemonsqueezy.com");

            // Paginate through all discounts to find the one with matching code
            int page = 1;
            const int maxPages = 10; // Safety limit

            while (page <= maxPages)
            {
                var request = new RestRequest($"/v1/discounts?filter[store_id]={storeId}&page[number]={page}&page[size]=50", Method.Get);
                request.AddHeader("Accept", "application/vnd.api+json");
                request.AddHeader("Content-Type", "application/vnd.api+json");
                request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

                var response = await restclient.ExecuteAsync(request);

                if (!response.IsSuccessful)
                {
                    logger.LogWarning("Failed to fetch discounts from LemonSqueezy: {StatusCode} {Content}",
                        response.StatusCode, response.Content);
                    return null;
                }

                var discountList = System.Text.Json.JsonSerializer.Deserialize<DiscountListResponse>(
                    response.Content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (discountList?.Data == null || discountList.Data.Length == 0)
                {
                    break; // No more results
                }

                // Find discount by code (case-insensitive)
                var discount = discountList.Data.FirstOrDefault(d =>
                    d.Attributes?.Code?.Equals(discountCode, StringComparison.OrdinalIgnoreCase) == true);

                if (discount != null)
                {
                    var attrs = discount.Attributes;

                    // Validate the discount is active and not expired
                    if (attrs.Status != "published")
                    {
                        logger.LogInformation("Discount code {Code} is not published (status: {Status})",
                            discountCode, attrs.Status);
                        return null;
                    }

                    if (attrs.StartsAt.HasValue && attrs.StartsAt.Value > DateTime.UtcNow)
                    {
                        logger.LogInformation("Discount code {Code} has not started yet", discountCode);
                        return null;
                    }

                    if (attrs.ExpiresAt.HasValue && attrs.ExpiresAt.Value < DateTime.UtcNow)
                    {
                        logger.LogInformation("Discount code {Code} has expired", discountCode);
                        return null;
                    }

                    // Determine if this is a subscription-only discount
                    // - Discounts with duration "repeating" or "forever" are for subscription renewals
                    // - Discounts limited to specific products may be subscription-only (need to check variants)
                    // For discounts limited to products, we need to check if they're limited to subscription variants
                    var isSubscriptionOnly = false;

                    if (attrs.IsLimitedToProducts)
                    {
                        // Fetch the single discount with included variants (more reliable than /variants endpoint)
                        var discountDetailRequest = new RestRequest($"/v1/discounts/{discount.Id}?include=variants", Method.Get);
                        discountDetailRequest.AddHeader("Accept", "application/vnd.api+json");
                        discountDetailRequest.AddHeader("Content-Type", "application/vnd.api+json");
                        discountDetailRequest.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

                        var discountDetailResponse = await restclient.ExecuteAsync(discountDetailRequest);
                        if (discountDetailResponse.IsSuccessful)
                        {
                            // Check if any of the limited variants match subscription variant IDs
                            var subscriptionVariantId = config["LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID"];
                            var quarterSubscriptionVariantId = config["LEMONSQUEEZY:QUARTER_SUBSCRIPTION_VARIANT_ID"];
                            var yearSubscriptionVariantId = config["LEMONSQUEEZY:YEAR_SUBSCRIPTION_VARIANT_ID"];
                            var regularVariantId = config["LEMONSQUEEZY:VARIANT_ID"];

                            var detailJson = System.Text.Json.JsonDocument.Parse(discountDetailResponse.Content);
                            var variantIds = new List<string>();

                            // Get variant IDs from relationships.variants.data array
                            if (detailJson.RootElement.TryGetProperty("data", out var dataObj) &&
                                dataObj.TryGetProperty("relationships", out var relationships) &&
                                relationships.TryGetProperty("variants", out var variantsRel) &&
                                variantsRel.TryGetProperty("data", out var variantsDataArray))
                            {
                                foreach (var variant in variantsDataArray.EnumerateArray())
                                {
                                    if (variant.TryGetProperty("id", out var idProp))
                                    {
                                        variantIds.Add(idProp.GetString());
                                    }
                                }
                            }

                            // If limited to subscription variants only (not regular variant)
                            var hasSubscriptionVariant = variantIds.Contains(subscriptionVariantId)
                                || variantIds.Contains(quarterSubscriptionVariantId)
                                || variantIds.Contains(yearSubscriptionVariantId);
                            var hasRegularVariant = variantIds.Contains(regularVariantId);

                            isSubscriptionOnly = hasSubscriptionVariant && !hasRegularVariant;

                            logger.LogInformation("Discount {Code} limited to variants: {Variants}, isSubscriptionOnly: {IsSubOnly}",
                                discountCode, string.Join(",", variantIds), isSubscriptionOnly);
                        }
                        else
                        {
                            logger.LogWarning("Failed to fetch discount details with variants: {StatusCode} {Content}",
                                discountDetailResponse.StatusCode, discountDetailResponse.Content);
                        }
                    }

                    // If caller specified subscription type, validate it matches
                    if (isSubscription.HasValue)
                    {
                        if (isSubscriptionOnly && !isSubscription.Value)
                        {
                            logger.LogInformation("Discount code {Code} is only valid for subscriptions", discountCode);
                            return new ValidatedDiscount
                            {
                                Code = attrs.Code,
                                Id = discount.Id,
                                Amount = attrs.Amount,
                                AmountType = attrs.AmountType,
                                Name = "This discount code is only valid for subscriptions",
                                IsValid = false,
                                IsLimitedToProducts = attrs.IsLimitedToProducts,
                                IsSubscriptionOnly = isSubscriptionOnly,
                                Duration = attrs.Duration,
                                DurationInMonths = attrs.DurationInMonths
                            };
                        }
                    }

                    return new ValidatedDiscount
                    {
                        Code = attrs.Code,
                        Id = discount.Id,
                        Amount = attrs.Amount,
                        AmountType = attrs.AmountType,
                        Name = attrs.Name,
                        IsValid = true,
                        IsLimitedToProducts = attrs.IsLimitedToProducts,
                        IsSubscriptionOnly = isSubscriptionOnly,
                        Duration = attrs.Duration,
                        DurationInMonths = attrs.DurationInMonths
                    };
                }

                // Check if there are more pages
                if (discountList.Meta?.Page == null ||
                    discountList.Meta.Page.CurrentPage >= discountList.Meta.Page.LastPage)
                {
                    break;
                }

                page++;
            }

            logger.LogInformation("Discount code {Code} not found", discountCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating discount code {Code}", discountCode);
            return null;
        }
    }

    public async Task CancelSubscription(string subscriptionId)
    {
        var restclient = new RestClient($"https://api.lemonsqueezy.com/v1/subscriptions/{subscriptionId}");
        var request = CreateRequest(Method.Delete);
        var response = await restclient.ExecuteAsync(request);
        logger.LogInformation(response.Content);
    }

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// A subscription can be resumed if it was cancelled but hasn't reached its ends_at date yet.
    /// </summary>
    /// <param name="subscriptionId">The LemonSqueezy subscription ID</param>
    /// <returns>True if successfully resumed, false otherwise</returns>
    public async Task<bool> ResumeSubscription(string subscriptionId)
    {
        var restclient = new RestClient("https://api.lemonsqueezy.com");
        var request = new RestRequest($"/v1/subscriptions/{subscriptionId}", Method.Patch);
        request.AddHeader("Accept", "application/vnd.api+json");
        request.AddHeader("Content-Type", "application/vnd.api+json");
        request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

        var body = new
        {
            data = new
            {
                type = "subscriptions",
                id = subscriptionId,
                attributes = new
                {
                    cancelled = false
                }
            }
        };

        request.AddJsonBody(body);
        var response = await restclient.ExecuteAsync(request);

        if (!response.IsSuccessful)
        {
            logger.LogWarning("Failed to resume subscription {SubscriptionId}: {StatusCode} {Content}",
                subscriptionId, response.StatusCode, response.Content);
            throw new ApiException($"Failed to resume subscription {subscriptionId}: {response.StatusCode} {response.Content}");
        }

        logger.LogInformation("Successfully resumed subscription {SubscriptionId}", subscriptionId);
        return true;
    }

    /// <summary>
    /// Get all invoices for a subscription
    /// </summary>
    /// <param name="subscriptionId">The LemonSqueezy subscription ID</param>
    /// <returns>List of subscription invoices</returns>
    public async Task<List<SubscriptionInvoice>> GetSubscriptionInvoicesAsync(string subscriptionId)
    {
        var restclient = new RestClient("https://api.lemonsqueezy.com");
        var invoices = new List<SubscriptionInvoice>();
        int page = 1;
        const int maxPages = 10;

        while (page <= maxPages)
        {
            var request = new RestRequest($"/v1/subscription-invoices?filter[subscription_id]={subscriptionId}&page[number]={page}&page[size]=50", Method.Get);
            request.AddHeader("Accept", "application/vnd.api+json");
            request.AddHeader("Content-Type", "application/vnd.api+json");
            request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

            var response = await restclient.ExecuteAsync(request);

            if (!response.IsSuccessful)
            {
                logger.LogWarning("Failed to fetch subscription invoices: {StatusCode} {Content}",
                    response.StatusCode, response.Content);
                break;
            }

            var json = System.Text.Json.JsonDocument.Parse(response.Content);

            if (!json.RootElement.TryGetProperty("data", out var dataArray) || dataArray.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in dataArray.EnumerateArray())
            {
                var attrs = item.GetProperty("attributes");
                var invoice = new SubscriptionInvoice
                {
                    Id = item.GetProperty("id").GetString(),
                    SubscriptionId = attrs.GetProperty("subscription_id").GetInt32(),
                    UserName = attrs.TryGetProperty("user_name", out var userName) ? userName.GetString() : null,
                    UserEmail = attrs.TryGetProperty("user_email", out var userEmail) ? userEmail.GetString() : null,
                    BillingReason = attrs.TryGetProperty("billing_reason", out var billingReason) ? billingReason.GetString() : null,
                    CardBrand = attrs.TryGetProperty("card_brand", out var cardBrand) ? cardBrand.GetString() : null,
                    CardLastFour = attrs.TryGetProperty("card_last_four", out var cardLastFour) ? cardLastFour.GetString() : null,
                    Currency = attrs.TryGetProperty("currency", out var currency) ? currency.GetString() : null,
                    Status = attrs.TryGetProperty("status", out var status) ? status.GetString() : null,
                    StatusFormatted = attrs.TryGetProperty("status_formatted", out var statusFormatted) ? statusFormatted.GetString() : null,
                    Refunded = attrs.TryGetProperty("refunded", out var refunded) && refunded.GetBoolean(),
                    Subtotal = attrs.TryGetProperty("subtotal", out var subtotal) ? subtotal.GetInt32() : 0,
                    DiscountTotal = attrs.TryGetProperty("discount_total", out var discountTotal) ? discountTotal.GetInt32() : 0,
                    Tax = attrs.TryGetProperty("tax", out var tax) ? tax.GetInt32() : 0,
                    Total = attrs.TryGetProperty("total", out var total) ? total.GetInt32() : 0,
                    SubtotalFormatted = attrs.TryGetProperty("subtotal_formatted", out var subtotalFormatted) ? subtotalFormatted.GetString() : null,
                    TotalFormatted = attrs.TryGetProperty("total_formatted", out var totalFormatted) ? totalFormatted.GetString() : null,
                    CreatedAt = attrs.TryGetProperty("created_at", out var createdAt) ? DateTime.Parse(createdAt.GetString()) : DateTime.MinValue,
                    UpdatedAt = attrs.TryGetProperty("updated_at", out var updatedAt) ? DateTime.Parse(updatedAt.GetString()) : DateTime.MinValue
                };

                // Get invoice URL from urls object
                if (attrs.TryGetProperty("urls", out var urls) && urls.TryGetProperty("invoice_url", out var invoiceUrl))
                {
                    invoice.InvoiceUrl = invoiceUrl.GetString();
                }

                invoices.Add(invoice);
            }

            // Check if there are more pages
            if (json.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("page", out var pageInfo) &&
                pageInfo.TryGetProperty("lastPage", out var lastPage))
            {
                if (page >= lastPage.GetInt32())
                    break;
            }
            else
            {
                break;
            }

            page++;
        }

        return invoices;
    }

    /// <summary>
    /// Generate a download link for a subscription invoice
    /// </summary>
    /// <param name="invoiceId">The invoice ID</param>
    /// <param name="request">The invoice generation request with address details</param>
    /// <returns>The download URL or null if failed</returns>
    public async Task<string> GenerateInvoiceDownloadLinkAsync(string invoiceId, GenerateInvoiceRequest request)
    {
        var restclient = new RestClient("https://api.lemonsqueezy.com");

        // Build query parameters
        var queryParams = new List<string>
        {
            $"name={Uri.EscapeDataString(request.Name ?? "")}",
            $"address={Uri.EscapeDataString(request.Address ?? "")}",
            $"city={Uri.EscapeDataString(request.City ?? "")}",
            $"zip_code={Uri.EscapeDataString(request.ZipCode ?? "")}",
            $"country={Uri.EscapeDataString(request.Country ?? "")}"
        };

        if (!string.IsNullOrEmpty(request.State))
            queryParams.Add($"state={Uri.EscapeDataString(request.State)}");
        if (!string.IsNullOrEmpty(request.Notes))
            queryParams.Add($"notes={Uri.EscapeDataString(request.Notes)}");
        if (!string.IsNullOrEmpty(request.Locale))
            queryParams.Add($"locale={Uri.EscapeDataString(request.Locale)}");

        var queryString = string.Join("&", queryParams);
        var apiRequest = new RestRequest($"/v1/subscription-invoices/{invoiceId}/generate-invoice?{queryString}", Method.Post);
        apiRequest.AddHeader("Accept", "application/vnd.api+json");
        apiRequest.AddHeader("Content-Type", "application/vnd.api+json");
        apiRequest.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

        var response = await restclient.ExecuteAsync(apiRequest);

        if (!response.IsSuccessful)
        {
            logger.LogWarning("Failed to generate invoice download link for invoice {InvoiceId}: {StatusCode} {Content}",
                invoiceId, response.StatusCode, response.Content);
            return null;
        }

        var json = System.Text.Json.JsonDocument.Parse(response.Content);

        if (json.RootElement.TryGetProperty("meta", out var meta) &&
            meta.TryGetProperty("urls", out var urls) &&
            urls.TryGetProperty("download_invoice", out var downloadUrl))
        {
            return downloadUrl.GetString();
        }

        logger.LogWarning("Invoice download URL not found in response for invoice {InvoiceId}", invoiceId);
        return null;
    }

    /// <summary>
    /// Refund a subscription invoice payment. Refunds can only be issued up to 3 days after the invoice was created.
    /// </summary>
    /// <param name="invoiceId">The subscription invoice ID</param>
    /// <param name="amountInCents">Optional refund amount in cents. If not specified, a full refund will be issued.</param>
    /// <returns>RefundResponse containing the updated invoice details, or null if refund failed</returns>
    public async Task<RefundResponse> RefundInvoiceAsync(string invoiceId, int? amountInCents = null)
    {
        try
        {
            var restclient = new RestClient("https://api.lemonsqueezy.com");
            var request = new RestRequest($"/v1/subscription-invoices/{invoiceId}/refund", Method.Post);
            request.AddHeader("Accept", "application/vnd.api+json");
            request.AddHeader("Content-Type", "application/vnd.api+json");
            request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);

            // Build the request body
            var attributes = new Dictionary<string, object>();
            if (amountInCents.HasValue)
            {
                attributes["amount"] = amountInCents.Value;
            }

            var body = new
            {
                data = new
                {
                    type = "subscription-invoices",
                    id = invoiceId,
                    attributes = attributes
                }
            };

            request.AddJsonBody(body);

            var response = await restclient.ExecuteAsync(request);

            if (!response.IsSuccessful)
            {
                logger.LogWarning("Failed to refund subscription invoice {InvoiceId}: {StatusCode} {Content}",
                    invoiceId, response.StatusCode, response.Content);
                return null;
            }

            // Parse the response
            var json = System.Text.Json.JsonDocument.Parse(response.Content);

            if (!json.RootElement.TryGetProperty("data", out var dataObj))
            {
                logger.LogWarning("Invalid response format when refunding invoice {InvoiceId}", invoiceId);
                return null;
            }

            var attrs = dataObj.GetProperty("attributes");
            var refundResponse = new RefundResponse
            {
                Id = dataObj.GetProperty("id").GetString(),
                Refunded = attrs.TryGetProperty("refunded", out var refunded) && refunded.GetBoolean(),
                RefundedAmount = attrs.TryGetProperty("refunded_amount", out var refundedAmount) ? refundedAmount.GetInt32() : 0,
                RefundedAmountFormatted = attrs.TryGetProperty("refunded_amount_formatted", out var refundedAmountFormatted) ? refundedAmountFormatted.GetString() : null,
                Status = attrs.TryGetProperty("status", out var status) ? status.GetString() : null
            };

            logger.LogInformation("Successfully refunded invoice {InvoiceId}: amount={Amount}, formatted={Formatted}",
                invoiceId, refundResponse.RefundedAmount, refundResponse.RefundedAmountFormatted);

            return refundResponse;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refunding subscription invoice {InvoiceId}", invoiceId);
            return null;
        }
    }

    public async Task<TopUpIdResponse> SetupPayment(TopUpOptions options, User user, Product product, decimal eurPrice, decimal coinAmount, string variantId, bool isSubscription, ValidatedDiscount validatedDiscount = null, bool enableTrial = false, int trialLengthDays = 3)
    {
        var restclient = new RestClient("https://api.lemonsqueezy.com/v1/checkouts");
        RestRequest request = CreateRequest(Method.Post);

        // Use the pre-calculated eurPrice (already includes any discounts from creator code)
        var finalPrice = eurPrice;
        string creatorCodeId = null;
        string discountCode = null;

        if (!string.IsNullOrWhiteSpace(options?.CreatorCode))
        {
            // Creator code will be validated and applied in the callback/webhook handler
            // Store the code in custom data for later processing
            creatorCodeId = options.CreatorCode;
        }

        // If a LemonSqueezy discount code is provided, pass it to the checkout
        if (validatedDiscount != null && validatedDiscount.IsValid)
        {
            discountCode = validatedDiscount.Code;
            logger.LogInformation("Applying LemonSqueezy discount code {Code} to checkout", discountCode);
        }

        // Extract country code from locale (e.g., "en-US" -> "US", "de-DE" -> "DE")
        string countryCode = null;
        if (!string.IsNullOrWhiteSpace(options?.Locale) && options.Locale.Contains('-'))
        {
            countryCode = options.Locale.Split('-').Last().ToUpperInvariant();
        }

        // Validate and cap trial length (max 3 days)
        var effectiveTrialDays = Math.Min(Math.Max(trialLengthDays, 1), 3);

        // Build checkout_options dynamically based on whether trial is enabled
        // skip_trial: if true, removes free trial even if product has one configured
        // For subscriptions with trial enabled, we don't skip trial
        // For subscriptions without trial or non-subscriptions, we skip any configured trial
        var checkoutOptions = new Dictionary<string, object>
        {
            { "subscription_preview", true },
            { "discount", !string.IsNullOrEmpty(discountCode) },
            { "skip_trial", true}
        };

        // Only set skip_trial if this is a subscription and trial is NOT enabled
        // (we want to skip any default product trial if the user hasn't opted into trial)
        if (enableTrial)
        {
            checkoutOptions["skip_trial"] = false;
        }

        var createData = new
        {
            data = new
            {
                type = "checkouts",
                attributes = new
                {
                    custom_price = (int)(finalPrice * 100),
                    product_options = new
                    {
                        name = product.Title,
                        redirect_url = options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"],
                        receipt_button_text = "Go to your account",
                        description = product.Description ?? "Will be credited to your account",
                        enabled_variants = new[] { int.Parse(variantId) } // Only show the selected variant
                    },
                    checkout_options = checkoutOptions,
                    checkout_data = new
                    {
                        email = options?.UserEmail,
                        discount_code = discountCode, // LemonSqueezy will apply this discount
                        billing_address = countryCode != null ? new { country = countryCode } : null,
                        custom = new
                        {
                            user_id = user.ExternalId.ToString(),
                            product_id = product.Id.ToString(),
                            coin_amount = ((int)coinAmount).ToString(),
                            is_subscription = isSubscription.ToString(),
                            creator_code = creatorCodeId,
                            enable_trial = enableTrial.ToString(),
                            trial_length_days = effectiveTrialDays.ToString()
                        },
                    },
                    expires_at = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                relationships = new
                {
                    store = new
                    {
                        data = new
                        {
                            type = "stores",
                            id = config["LEMONSQUEEZY:STORE_ID"]
                        }
                    },
                    variant = new
                    {
                        data = new
                        {
                            type = "variants",
                            id = variantId
                        }
                    }
                }
            }
        };
        var json = JsonConvert.SerializeObject(createData, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
        request.AddJsonBody(json);
        logger.LogInformation($"Creating lemonsqueezy checkout with: \n{json}");
        var response = await restclient.ExecuteAsync(request);
        logger.LogInformation(response.StatusCode + response.Content);
        var result = JsonConvert.DeserializeObject(response.Content);
        var data = JObject.Parse(result.ToString());
        var checkoutId = (string)data["data"]["id"];
        var link = (string)data["data"]["attributes"]["url"];
        return new TopUpIdResponse()
        {
            DirctLink = link,
            Id = checkoutId
        };
    }

    private RestRequest CreateRequest(Method method)
    {
        var request = new RestRequest("", method);
        request.AddHeader("Accept", "application/vnd.api+json");
        request.AddHeader("Content-Type", "application/vnd.api+json");
        request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);
        Console.WriteLine("Using API Key: " + config["LEMONSQUEEZY:API_KEY"]);
        return request;
    }
}
