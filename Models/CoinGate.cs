using System;
using System.Text.Json.Serialization;

namespace Coflnet.Payments.Models.CoinGate;

/// <summary>
/// Request to create a CoinGate order
/// </summary>
public class CoinGateCreateOrderRequest
{
    /// <summary>
    /// Merchant's custom order ID. Should be unique.
    /// </summary>
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; }

    /// <summary>
    /// The price set by the merchant (required)
    /// </summary>
    [JsonPropertyName("price_amount")]
    public decimal PriceAmount { get; set; }

    /// <summary>
    /// ISO 4217 currency code (required)
    /// </summary>
    [JsonPropertyName("price_currency")]
    public string PriceCurrency { get; set; }

    /// <summary>
    /// Currency to receive settlement in (EUR, USD, BTC, DO_NOT_CONVERT, etc.)
    /// </summary>
    [JsonPropertyName("receive_currency")]
    public string ReceiveCurrency { get; set; }

    /// <summary>
    /// Title of the order (required, 3-150 chars)
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; }

    /// <summary>
    /// Description of the order (required, 3-500 chars)
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; }

    /// <summary>
    /// URL to receive callbacks when order status changes
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string CallbackUrl { get; set; }

    /// <summary>
    /// URL to redirect buyer on cancel
    /// </summary>
    [JsonPropertyName("cancel_url")]
    public string CancelUrl { get; set; }

    /// <summary>
    /// URL to redirect buyer on success
    /// </summary>
    [JsonPropertyName("success_url")]
    public string SuccessUrl { get; set; }

    /// <summary>
    /// Custom token to validate payment callbacks
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; }

    /// <summary>
    /// Shopper email address
    /// </summary>
    [JsonPropertyName("purchaser_email")]
    public string PurchaserEmail { get; set; }
}

/// <summary>
/// Response from CoinGate create order API
/// </summary>
public class CoinGateOrderResponse
{
    /// <summary>
    /// CoinGate order (invoice) ID
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>
    /// Merchant's custom order ID
    /// </summary>
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; }

    /// <summary>
    /// Order status (new, pending, confirming, paid, invalid, expired, canceled, refunded)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }

    /// <summary>
    /// Price amount set by merchant
    /// </summary>
    [JsonPropertyName("price_amount")]
    public decimal PriceAmount { get; set; }

    /// <summary>
    /// Price currency
    /// </summary>
    [JsonPropertyName("price_currency")]
    public string PriceCurrency { get; set; }

    /// <summary>
    /// Currency to receive settlement in
    /// </summary>
    [JsonPropertyName("receive_currency")]
    public string ReceiveCurrency { get; set; }

    /// <summary>
    /// Amount to be received after fees
    /// </summary>
    [JsonPropertyName("receive_amount")]
    public decimal? ReceiveAmount { get; set; }

    /// <summary>
    /// Amount the customer needs to pay in cryptocurrency
    /// </summary>
    [JsonPropertyName("pay_amount")]
    public decimal? PayAmount { get; set; }

    /// <summary>
    /// Cryptocurrency the customer will pay with
    /// </summary>
    [JsonPropertyName("pay_currency")]
    public string PayCurrency { get; set; }

    /// <summary>
    /// Underpaid amount in pay_currency
    /// </summary>
    [JsonPropertyName("underpaid_amount")]
    public decimal? UnderpaidAmount { get; set; }

    /// <summary>
    /// Overpaid amount in pay_currency
    /// </summary>
    [JsonPropertyName("overpaid_amount")]
    public decimal? OverpaidAmount { get; set; }

    /// <summary>
    /// Whether the order is refundable
    /// </summary>
    [JsonPropertyName("is_refundable")]
    public bool IsRefundable { get; set; }

    /// <summary>
    /// Order creation time
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Order expiration time
    /// </summary>
    [JsonPropertyName("expire_at")]
    public DateTime? ExpireAt { get; set; }

    /// <summary>
    /// Time when the order was paid
    /// </summary>
    [JsonPropertyName("paid_at")]
    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// Payment URL to redirect the customer to
    /// </summary>
    [JsonPropertyName("payment_url")]
    public string PaymentUrl { get; set; }

    /// <summary>
    /// Payment address for cryptocurrency
    /// </summary>
    [JsonPropertyName("payment_address")]
    public string PaymentAddress { get; set; }

    /// <summary>
    /// Custom token for callback validation
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; }
}

/// <summary>
/// CoinGate payment callback data
/// </summary>
public class CoinGateCallback
{
    /// <summary>
    /// CoinGate order (invoice) ID
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>
    /// Merchant's custom order ID
    /// </summary>
    [JsonPropertyName("order_id")]
    public string OrderId { get; set; }

    /// <summary>
    /// Payment status (pending, confirming, paid, invalid, expired, canceled, refunded)
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; }

    /// <summary>
    /// Price amount set by merchant
    /// </summary>
    [JsonPropertyName("price_amount")]
    public decimal PriceAmount { get; set; }

    /// <summary>
    /// Price currency
    /// </summary>
    [JsonPropertyName("price_currency")]
    public string PriceCurrency { get; set; }

    /// <summary>
    /// Currency to receive settlement in
    /// </summary>
    [JsonPropertyName("receive_currency")]
    public string ReceiveCurrency { get; set; }

    /// <summary>
    /// Amount received after fees
    /// </summary>
    [JsonPropertyName("receive_amount")]
    public decimal? ReceiveAmount { get; set; }

    /// <summary>
    /// Amount paid by shopper in cryptocurrency
    /// </summary>
    [JsonPropertyName("pay_amount")]
    public decimal? PayAmount { get; set; }

    /// <summary>
    /// Cryptocurrency used for payment
    /// </summary>
    [JsonPropertyName("pay_currency")]
    public string PayCurrency { get; set; }

    /// <summary>
    /// Underpaid amount in pay_currency
    /// </summary>
    [JsonPropertyName("underpaid_amount")]
    public decimal? UnderpaidAmount { get; set; }

    /// <summary>
    /// Overpaid amount in pay_currency
    /// </summary>
    [JsonPropertyName("overpaid_amount")]
    public decimal? OverpaidAmount { get; set; }

    /// <summary>
    /// Whether a refund can be requested
    /// </summary>
    [JsonPropertyName("is_refundable")]
    public bool? IsRefundable { get; set; }

    /// <summary>
    /// Order creation time
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Time when the order was paid
    /// </summary>
    [JsonPropertyName("paid_at")]
    public DateTime? PaidAt { get; set; }

    /// <summary>
    /// Custom token for callback validation
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; }
}

/// <summary>
/// Order status values in CoinGate
/// </summary>
public static class CoinGateOrderStatus
{
    /// <summary>
    /// Order created, waiting for payment
    /// </summary>
    public const string New = "new";

    /// <summary>
    /// Payment detected, waiting for blockchain confirmation
    /// </summary>
    public const string Pending = "pending";

    /// <summary>
    /// Payment confirming on blockchain
    /// </summary>
    public const string Confirming = "confirming";

    /// <summary>
    /// Payment successfully completed
    /// </summary>
    public const string Paid = "paid";

    /// <summary>
    /// Payment invalid (underpaid, etc.)
    /// </summary>
    public const string Invalid = "invalid";

    /// <summary>
    /// Order expired without payment
    /// </summary>
    public const string Expired = "expired";

    /// <summary>
    /// Order was canceled
    /// </summary>
    public const string Canceled = "canceled";

    /// <summary>
    /// Order was refunded
    /// </summary>
    public const string Refunded = "refunded";
}
