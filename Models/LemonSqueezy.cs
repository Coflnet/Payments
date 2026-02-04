using System.Text.Json.Serialization;

namespace Coflnet.Payments.Models.LemonSqueezy;

// Product API models for variant discovery
public class ProductListResponse
{
    [JsonPropertyName("data")]
    public ProductData[] Data { get; set; }
    
    [JsonPropertyName("meta")]
    public MetaData Meta { get; set; }
}

public class ProductData
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("attributes")]
    public ProductAttributes Attributes { get; set; }
}

public class ProductAttributes
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("slug")]
    public string Slug { get; set; }
    
    [JsonPropertyName("status")]
    public string Status { get; set; }
}

public class VariantListResponse
{
    [JsonPropertyName("data")]
    public VariantData[] Data { get; set; }
}

public class VariantData
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("attributes")]
    public VariantAttributes Attributes { get; set; }
}

public class VariantAttributes
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("is_subscription")]
    public bool IsSubscription { get; set; }
    
    [JsonPropertyName("interval")]
    public string Interval { get; set; }
    
    [JsonPropertyName("interval_count")]
    public int IntervalCount { get; set; }
    
    [JsonPropertyName("price")]
    public int Price { get; set; }
    
    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }
}

public class MetaData
{
    [JsonPropertyName("page")]
    public PageMetaInfo Page { get; set; }
}

public class PageMetaInfo
{
    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; set; }
    
    [JsonPropertyName("lastPage")]
    public int LastPage { get; set; }
}

// Root myDeserializedClass = JsonSerializer.Deserialize<Root>(myJsonResponse);
public class Attributes
{
    [JsonConstructor]
    public Attributes(
        int storeId,
        int customerId,
        string identifier,
        int orderNumber,
        string userName,
        string userEmail,
        string currency,
        string currencyRate,
        string taxName,
        double taxRate,
        string status,
        string statusFormatted,
        bool refunded,
        object refundedAt,
        int subtotal,
        int discountTotal,
        int tax,
        int total,
        int subtotalUsd,
        int discountTotalUsd,
        int taxUsd,
        int totalUsd,
        string subtotalFormatted,
        string discountTotalFormatted,
        string taxFormatted,
        string totalFormatted,
        FirstOrderItem firstOrderItem,
        Urls urls,
        DateTime createdAt,
        DateTime updatedAt,
        bool testMode,
        long subscriptionId,
        DateTime? renewsAt,
        DateTime? endsAt,
        string billingReason = null)
    {
        this.StoreId = storeId;
        this.CustomerId = customerId;
        this.Identifier = identifier;
        this.OrderNumber = orderNumber;
        this.UserName = userName;
        this.UserEmail = userEmail;
        this.Currency = currency;
        this.CurrencyRate = currencyRate;
        this.TaxName = taxName;
        this.TaxRate = taxRate;
        this.Status = status;
        this.StatusFormatted = statusFormatted;
        this.Refunded = refunded;
        this.RefundedAt = refundedAt;
        this.Subtotal = subtotal;
        this.DiscountTotal = discountTotal;
        this.Tax = tax;
        this.Total = total;
        this.SubtotalUsd = subtotalUsd;
        this.DiscountTotalUsd = discountTotalUsd;
        this.TaxUsd = taxUsd;
        this.TotalUsd = totalUsd;
        this.SubtotalFormatted = subtotalFormatted;
        this.DiscountTotalFormatted = discountTotalFormatted;
        this.TaxFormatted = taxFormatted;
        this.TotalFormatted = totalFormatted;
        this.FirstOrderItem = firstOrderItem;
        this.Urls = urls;
        this.CreatedAt = createdAt;
        this.UpdatedAt = updatedAt;
        this.TestMode = testMode;
        this.SubscriptionId = subscriptionId;
        RenewsAt = renewsAt;
        EndsAt = endsAt;
        BillingReason = billingReason;
    }

    [JsonPropertyName("store_id")]
    public int StoreId { get; }

    [JsonPropertyName("customer_id")]
    public int CustomerId { get; }

    [JsonPropertyName("identifier")]
    public string Identifier { get; }

    [JsonPropertyName("order_number")]
    public int OrderNumber { get; }

    [JsonPropertyName("user_name")]
    public string UserName { get; }

    [JsonPropertyName("user_email")]
    public string UserEmail { get; }

    [JsonPropertyName("currency")]
    public string Currency { get; }

    [JsonPropertyName("currency_rate")]
    public string CurrencyRate { get; }

    [JsonPropertyName("tax_name")]
    public string TaxName { get; }

    [JsonPropertyName("tax_rate")]
    public double TaxRate { get; }

    [JsonPropertyName("status")]
    public string Status { get; }

    [JsonPropertyName("status_formatted")]
    public string StatusFormatted { get; }

    [JsonPropertyName("refunded")]
    public bool Refunded { get; }

    [JsonPropertyName("refunded_at")]
    public object RefundedAt { get; }

    [JsonPropertyName("subtotal")]
    public int Subtotal { get; }

    [JsonPropertyName("discount_total")]
    public int DiscountTotal { get; }

    [JsonPropertyName("tax")]
    public int Tax { get; }

    [JsonPropertyName("total")]
    public int Total { get; }

    [JsonPropertyName("subtotal_usd")]
    public int SubtotalUsd { get; }

    [JsonPropertyName("discount_total_usd")]
    public int DiscountTotalUsd { get; }

    [JsonPropertyName("tax_usd")]
    public int TaxUsd { get; }

    [JsonPropertyName("total_usd")]
    public int TotalUsd { get; }

    [JsonPropertyName("subtotal_formatted")]
    public string SubtotalFormatted { get; }

    [JsonPropertyName("discount_total_formatted")]
    public string DiscountTotalFormatted { get; }

    [JsonPropertyName("tax_formatted")]
    public string TaxFormatted { get; }

    [JsonPropertyName("total_formatted")]
    public string TotalFormatted { get; }

    [JsonPropertyName("first_order_item")]
    public FirstOrderItem FirstOrderItem { get; }

    [JsonPropertyName("urls")]
    public Urls Urls { get; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; }

    [JsonPropertyName("test_mode")]
    public bool TestMode { get; }
    [JsonPropertyName("subscription_id")]
    public long SubscriptionId { get; }
    [JsonPropertyName("renews_at")]
    public DateTime? RenewsAt { get; set; }
    [JsonPropertyName("ends_at")]
    public DateTime? EndsAt { get; set; }
    [JsonPropertyName("trial_ends_at")]
    public DateTime? TrialEndsAt { get; set; }
    [JsonPropertyName("payment_processor")]
    public string PaymentProcessor { get; set; }
    
    /// <summary>
    /// Billing reason for subscription invoices. Values: "initial" for first payment, "renewal" for subsequent payments.
    /// Only present in subscription_payment_success webhooks (type: subscription-invoices).
    /// </summary>
    [JsonPropertyName("billing_reason")]
    public string BillingReason { get; set; }
}


// todo: add  product_id  & coin_amount
public class CustomData
{
    [JsonConstructor]
    public CustomData(
        string userId,
        int productId,
        long coinAmount,
        string isSubscription,
        string creatorCode = null)
    {
        this.UserId = userId;
        this.ProductId = productId;
        this.CoinAmount = coinAmount;
        IsSubscription = isSubscription;
        CreatorCode = creatorCode;
    }

    [JsonPropertyName("user_id")]
    public string UserId { get; }
    [JsonPropertyName("product_id")]
    public int ProductId { get; }
    [JsonPropertyName("coin_amount")]
    public long CoinAmount { get; }
    [JsonPropertyName("is_subscription")]
    public string IsSubscription { get; }
    [JsonPropertyName("creator_code")]
    public string CreatorCode { get; }
}

public class Customer
{
    [JsonConstructor]
    public Customer(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class Data
{
    [JsonConstructor]
    public Data(
        string type,
        string id,
        Attributes attributes,
        Relationships relationships,
        Links links
    )
    {
        this.Type = type;
        this.Id = id;
        this.Attributes = attributes;
        this.Relationships = relationships;
        this.Links = links;
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("id")]
    public string Id { get; }

    [JsonPropertyName("attributes")]
    public Attributes Attributes { get; }

    [JsonPropertyName("relationships")]
    public Relationships Relationships { get; }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class DiscountRedemptions
{
    [JsonConstructor]
    public DiscountRedemptions(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class FirstOrderItem
{
    [JsonConstructor]
    public FirstOrderItem(
        int id,
        int orderId,
        int productId,
        int variantId,
        int priceId,
        string productName,
        string variantName,
        int price,
        DateTime createdAt,
        DateTime updatedAt,
        bool testMode
    )
    {
        this.Id = id;
        this.OrderId = orderId;
        this.ProductId = productId;
        this.VariantId = variantId;
        this.PriceId = priceId;
        this.ProductName = productName;
        this.VariantName = variantName;
        this.Price = price;
        this.CreatedAt = createdAt;
        this.UpdatedAt = updatedAt;
        this.TestMode = testMode;
    }

    [JsonPropertyName("id")]
    public int Id { get; }

    [JsonPropertyName("order_id")]
    public int OrderId { get; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; }

    [JsonPropertyName("variant_id")]
    public int VariantId { get; }

    [JsonPropertyName("price_id")]
    public int PriceId { get; }

    [JsonPropertyName("product_name")]
    public string ProductName { get; }

    [JsonPropertyName("variant_name")]
    public string VariantName { get; }

    [JsonPropertyName("price")]
    public int Price { get; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; }

    [JsonPropertyName("test_mode")]
    public bool TestMode { get; }
}

public class LicenseKeys
{
    [JsonConstructor]
    public LicenseKeys(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class Links
{
    [JsonConstructor]
    public Links(
        string related,
        string self
    )
    {
        this.Related = related;
        this.Self = self;
    }

    [JsonPropertyName("related")]
    public string Related { get; }

    [JsonPropertyName("self")]
    public string Self { get; }
}

public class Meta
{
    [JsonConstructor]
    public Meta(
        bool testMode,
        string eventName,
        CustomData customData
    )
    {
        this.TestMode = testMode;
        this.EventName = eventName;
        this.CustomData = customData;
    }

    [JsonPropertyName("test_mode")]
    public bool TestMode { get; }

    [JsonPropertyName("event_name")]
    public string EventName { get; }

    [JsonPropertyName("custom_data")]
    public CustomData CustomData { get; }
}

public class OrderItems
{
    [JsonConstructor]
    public OrderItems(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class Relationships
{
    [JsonConstructor]
    public Relationships(
        Store store,
        Customer customer,
        OrderItems orderItems,
        Subscriptions subscriptions,
        LicenseKeys licenseKeys,
        DiscountRedemptions discountRedemptions
    )
    {
        this.Store = store;
        this.Customer = customer;
        this.OrderItems = orderItems;
        this.Subscriptions = subscriptions;
        this.LicenseKeys = licenseKeys;
        this.DiscountRedemptions = discountRedemptions;
    }

    [JsonPropertyName("store")]
    public Store Store { get; }

    [JsonPropertyName("customer")]
    public Customer Customer { get; }

    [JsonPropertyName("order-items")]
    public OrderItems OrderItems { get; }

    [JsonPropertyName("subscriptions")]
    public Subscriptions Subscriptions { get; }

    [JsonPropertyName("license-keys")]
    public LicenseKeys LicenseKeys { get; }

    [JsonPropertyName("discount-redemptions")]
    public DiscountRedemptions DiscountRedemptions { get; }
}

public class Webhook
{
    [JsonConstructor]
    public Webhook(
        Meta meta,
        Data data
    )
    {
        this.Meta = meta;
        this.Data = data;
    }

    [JsonPropertyName("meta")]
    public Meta Meta { get; }

    [JsonPropertyName("data")]
    public Data Data { get; }
}

public class Store
{
    [JsonConstructor]
    public Store(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class Subscriptions
{
    [JsonConstructor]
    public Subscriptions(
        Links links
    )
    {
        this.Links = links;
    }

    [JsonPropertyName("links")]
    public Links Links { get; }
}

public class Urls
{
    [JsonConstructor]
    public Urls(
        string receipt
    )
    {
        this.Receipt = receipt;
    }

    [JsonPropertyName("receipt")]
    public string Receipt { get; }
}

