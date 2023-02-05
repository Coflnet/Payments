using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Coflnet.Payments.Models;

/// <summary>
/// An user capable of making transactions  
/// </summary>
public class User
{
    /// <summary>
    /// primary key
    /// </summary>
    /// <value></value>
    public int Id { get; set; }
    /// <summary>
    /// The identifier of the account system
    /// </summary>
    /// <value></value>
    [MaxLength(32)]
    public string ExternalId { get; set; }
    /// <summary>
    /// Balance of this user
    /// </summary>
    /// <value></value>
    public decimal Balance { get; set; }
    /// <summary>
    /// The balance available (<see cref="Balance"/> minus any <see cref="PlanedTransaction"/>)
    /// </summary>
    /// <value></value>
    [NotMapped]
    public decimal AvailableBalance { get; set; }
    /// <summary>
    /// Things this user owns
    /// </summary>
    /// <value></value>
    public List<OwnerShip> Owns { get; set; }
    /// <summary>
    /// Country this user is from (ISO 3166-1 alpha-2)
    /// </summary>
    [MaxLength(2)]
    public string Country { get; set; }
    /// <summary>
    /// The zip code of the user
    /// </summary>
    [MaxLength(10)]
    public string Zip { get; set; }
    /// <summary>
    /// The ip of the user
    /// </summary>
    [MaxLength(45)]
    public System.Net.IPAddress Ip { get; set; }
    /// <summary>
    /// Locale of the user
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }
}

public class PaymentRequest
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
        /// The payment request was failed
        /// </summary>
        FAILED,
    }
}