using System;
using System.Text.Json.Serialization;

namespace Coflnet.Payments.Models;

/// <summary>
/// Represents a subscription invoice from LemonSqueezy
/// </summary>
public class SubscriptionInvoice
{
    /// <summary>
    /// The invoice ID
    /// </summary>
    public string Id { get; set; }
    
    /// <summary>
    /// The subscription ID this invoice belongs to
    /// </summary>
    public int SubscriptionId { get; set; }
    
    /// <summary>
    /// Customer name
    /// </summary>
    public string UserName { get; set; }
    
    /// <summary>
    /// Customer email
    /// </summary>
    public string UserEmail { get; set; }
    
    /// <summary>
    /// Reason for billing (initial, renewal, etc.)
    /// </summary>
    public string BillingReason { get; set; }
    
    /// <summary>
    /// Card brand used for payment
    /// </summary>
    public string CardBrand { get; set; }
    
    /// <summary>
    /// Last four digits of the card
    /// </summary>
    public string CardLastFour { get; set; }
    
    /// <summary>
    /// Currency code
    /// </summary>
    public string Currency { get; set; }
    
    /// <summary>
    /// Invoice status (paid, pending, void, refunded, etc.)
    /// </summary>
    public string Status { get; set; }
    
    /// <summary>
    /// Human-readable status
    /// </summary>
    public string StatusFormatted { get; set; }
    
    /// <summary>
    /// Whether the invoice was refunded
    /// </summary>
    public bool Refunded { get; set; }
    
    /// <summary>
    /// Subtotal in cents
    /// </summary>
    public int Subtotal { get; set; }
    
    /// <summary>
    /// Discount total in cents
    /// </summary>
    public int DiscountTotal { get; set; }
    
    /// <summary>
    /// Tax amount in cents
    /// </summary>
    public int Tax { get; set; }
    
    /// <summary>
    /// Total amount in cents
    /// </summary>
    public int Total { get; set; }
    
    /// <summary>
    /// Formatted subtotal string
    /// </summary>
    public string SubtotalFormatted { get; set; }
    
    /// <summary>
    /// Formatted total string
    /// </summary>
    public string TotalFormatted { get; set; }
    
    /// <summary>
    /// URL to view the invoice online
    /// </summary>
    public string InvoiceUrl { get; set; }
    
    /// <summary>
    /// When the invoice was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When the invoice was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request model for generating an invoice download link
/// </summary>
public class GenerateInvoiceRequest
{
    /// <summary>
    /// The full name of the customer (required)
    /// </summary>
    public string Name { get; set; }
    
    /// <summary>
    /// The street address of the customer (required)
    /// </summary>
    public string Address { get; set; }
    
    /// <summary>
    /// The city of the customer (required)
    /// </summary>
    public string City { get; set; }
    
    /// <summary>
    /// The state of the customer (required for US and CA)
    /// </summary>
    public string State { get; set; }
    
    /// <summary>
    /// The ZIP code of the customer (required)
    /// </summary>
    public string ZipCode { get; set; }
    
    /// <summary>
    /// The country of the customer (required, ISO 3166-1 alpha-2)
    /// </summary>
    public string Country { get; set; }
    
    /// <summary>
    /// Any additional notes to include on the invoice
    /// </summary>
    public string Notes { get; set; }
    
    /// <summary>
    /// ISO 639 language code, e.g., 'en' for English
    /// </summary>
    public string Locale { get; set; }
}

/// <summary>
/// Response containing the invoice download URL
/// </summary>
public class InvoiceDownloadResponse
{
    /// <summary>
    /// URL to download the invoice PDF
    /// </summary>
    public string DownloadUrl { get; set; }
}
