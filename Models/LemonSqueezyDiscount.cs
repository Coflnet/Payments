using System;
using System.Text.Json.Serialization;

namespace Coflnet.Payments.Models.LemonSqueezy;

/// <summary>
/// Response wrapper for LemonSqueezy discount API
/// </summary>
public class DiscountResponse
{
    [JsonPropertyName("jsonapi")]
    public JsonApiVersion JsonApi { get; set; }

    [JsonPropertyName("links")]
    public DiscountLinks Links { get; set; }

    [JsonPropertyName("data")]
    public DiscountData Data { get; set; }
}

/// <summary>
/// Response wrapper for list of discounts from LemonSqueezy API
/// </summary>
public class DiscountListResponse
{
    [JsonPropertyName("jsonapi")]
    public JsonApiVersion JsonApi { get; set; }

    [JsonPropertyName("links")]
    public DiscountLinks Links { get; set; }

    [JsonPropertyName("data")]
    public DiscountData[] Data { get; set; }

    [JsonPropertyName("meta")]
    public DiscountMeta Meta { get; set; }
}

public class DiscountMeta
{
    [JsonPropertyName("page")]
    public PageInfo Page { get; set; }
}

public class PageInfo
{
    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("lastPage")]
    public int LastPage { get; set; }

    [JsonPropertyName("perPage")]
    public int PerPage { get; set; }

    [JsonPropertyName("to")]
    public int To { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public class JsonApiVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; }
}

public class DiscountLinks
{
    [JsonPropertyName("self")]
    public string Self { get; set; }
}

public class DiscountData
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("attributes")]
    public DiscountAttributes Attributes { get; set; }
}

public class DiscountAttributes
{
    [JsonPropertyName("store_id")]
    public int StoreId { get; set; }

    /// <summary>
    /// The name/description of the discount
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; }

    /// <summary>
    /// The discount code (e.g., "10PERC")
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; }

    /// <summary>
    /// The discount amount (percentage or fixed based on amount_type)
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Type of discount: "percent" or "fixed"
    /// </summary>
    [JsonPropertyName("amount_type")]
    public string AmountType { get; set; }

    /// <summary>
    /// Whether the discount is limited to specific products
    /// </summary>
    [JsonPropertyName("is_limited_to_products")]
    public bool IsLimitedToProducts { get; set; }

    /// <summary>
    /// Whether the discount has a max number of redemptions
    /// </summary>
    [JsonPropertyName("is_limited_redemptions")]
    public bool IsLimitedRedemptions { get; set; }

    /// <summary>
    /// Maximum number of redemptions allowed (0 = unlimited)
    /// </summary>
    [JsonPropertyName("max_redemptions")]
    public int MaxRedemptions { get; set; }

    /// <summary>
    /// When the discount becomes active
    /// </summary>
    [JsonPropertyName("starts_at")]
    public DateTime? StartsAt { get; set; }

    /// <summary>
    /// When the discount expires
    /// </summary>
    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Duration type for subscriptions: "once", "repeating", or "forever"
    /// </summary>
    [JsonPropertyName("duration")]
    public string Duration { get; set; }

    /// <summary>
    /// Number of months the discount applies (for "repeating" duration)
    /// </summary>
    [JsonPropertyName("duration_in_months")]
    public int DurationInMonths { get; set; }

    /// <summary>
    /// Current status: "draft" or "published"
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }

    /// <summary>
    /// Formatted status for display
    /// </summary>
    [JsonPropertyName("status_formatted")]
    public string StatusFormatted { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("test_mode")]
    public bool TestMode { get; set; }
}

/// <summary>
/// Validated discount information for use in checkout
/// </summary>
public class ValidatedDiscount
{
    /// <summary>
    /// The discount code
    /// </summary>
    public string Code { get; set; }

    /// <summary>
    /// The discount ID in LemonSqueezy
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// The discount amount (percentage or fixed)
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Type of discount: "percent" or "fixed"
    /// </summary>
    public string AmountType { get; set; }

    /// <summary>
    /// Name/description of the discount
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Whether the discount is currently valid
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Whether the discount is limited to specific products/variants
    /// </summary>
    public bool IsLimitedToProducts { get; set; }

    /// <summary>
    /// Whether the discount is only valid for subscriptions (duration != "once" or is limited to subscription variants)
    /// </summary>
    public bool IsSubscriptionOnly { get; set; }

    /// <summary>
    /// Duration type for subscriptions: "once", "repeating", or "forever"
    /// </summary>
    public string Duration { get; set; }

    /// <summary>
    /// Number of months the discount applies (for "repeating" duration)
    /// </summary>
    public int DurationInMonths { get; set; }

    /// <summary>
    /// Calculates the discounted price from the original price
    /// </summary>
    /// <param name="originalPrice">Original price in currency units (e.g., EUR)</param>
    /// <returns>Discounted price</returns>
    public decimal CalculateDiscountedPrice(decimal originalPrice)
    {
        if (AmountType == "percent")
        {
            return Math.Round(originalPrice * (1 - Amount / 100), 2);
        }
        else // fixed
        {
            // Fixed amount is in cents
            return Math.Max(0, originalPrice - Amount / 100);
        }
    }

    /// <summary>
    /// Calculates the discount amount for a given price
    /// </summary>
    /// <param name="originalPrice">Original price in currency units (e.g., EUR)</param>
    /// <returns>Discount amount</returns>
    public decimal CalculateDiscountAmount(decimal originalPrice)
    {
        if (AmountType == "percent")
        {
            return Math.Round(originalPrice * Amount / 100, 2);
        }
        else // fixed
        {
            // Fixed amount is in cents
            return Math.Min(originalPrice, Amount / 100);
        }
    }
}
