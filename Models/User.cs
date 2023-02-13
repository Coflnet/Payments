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
    public string Ip { get; set; }
    /// <summary>
    /// Locale of the user
    /// </summary>
    [MaxLength(5)]
    public string Locale { get; set; }
}