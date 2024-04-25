using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

public class PaymentRequest : HasId
{
    public int Id { get; set; }
    /// <summary>
    /// The amount to pay
    /// </summary>
    /// <value></value>
    public decimal Amount { get; set; }
    /// <summary>
    /// The id of the product to pay for
    /// </summary>
    /// <value></value>
    public TopUpProduct ProductId { get; set; }
    /// <summary>
    /// The id of the user to pay for
    /// </summary>
    /// <value></value>
    public User User { get; set; }
    /// <summary>
    /// The slug of the payment provider
    /// </summary>
    /// <value></value>
    [MaxLength(32)]
    public string Provider { get; set; }
    /// <summary>
    /// Session id from the payment provider
    /// </summary>
    [MaxLength(75)]
    public string SessionId { get; set; }
    /// <summary>
    /// The device ip the session was created with
    /// </summary>
    public System.Net.IPAddress CreateOnIp { get; set; }
    /// <summary>
    /// Locale of the device the session was created with
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }
    /// <summary>
    /// Device Fingerprint of the device the session was created with
    /// </summary>
    [MaxLength(32)]
    public string DeviceFingerprint { get; set; }
    /// <summary>
    /// The current status of the payment request
    /// </summary>
    public Status State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? UserId { get; set; }
    /// <summary>
    /// States a payment request can be in
    /// </summary>
    public enum Status
    {
        /// <summary>
        /// The payment request was created
        /// </summary>
        CREATED,
        /// <summary>
        /// The payment request was confirmed
        /// </summary>
        CONFIRMED,
        /// <summary>
        /// The payment request was rejected
        /// </summary>
        REJECTED,
        /// <summary>
        /// Waiting for the user to enter payment details
        /// </summary>
        WAITING,
        /// <summary>
        /// Payment is being processed
        /// </summary>
        PROCESSING,
        /// <summary>
        /// The payment request was paid
        /// </summary>
        PAID,
        /// <summary>
        /// The payment request was canceled
        /// </summary>
        CANCELED,
        /// <summary>
        /// The payment request was refunded
        /// </summary>
        REFUNDED,
        /// <summary>
        /// The payment request was expired
        /// </summary>
        EXPIRED,
        /// <summary>
        /// The payment request has failed
        /// </summary>
        FAILED,
    }
}