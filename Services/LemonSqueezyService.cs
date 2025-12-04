using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Models.LemonSqueezy;
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

    public LemonSqueezyService(IConfiguration config, ILogger<LemonSqueezyService> logger)
    {
        this.config = config;
        this.logger = logger;
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
                            var hasSubscriptionVariant = variantIds.Contains(subscriptionVariantId) || variantIds.Contains(yearSubscriptionVariantId);
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
            return false;
        }
        
        logger.LogInformation("Successfully resumed subscription {SubscriptionId}", subscriptionId);
        return true;
    }

    public async Task<TopUpIdResponse> SetupPayment(TopUpOptions options, User user, Product product, decimal eurPrice, decimal coinAmount, string variantId, bool isSubscription, ValidatedDiscount validatedDiscount = null)
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
                    },
                    checkout_options = new
                    {
                        subscription_preview = true,
                        discount = !string.IsNullOrEmpty(discountCode) // show discount field if code provided
                    },
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
                            creator_code = creatorCodeId
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
        logger.LogInformation(response.Content);
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
