using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

public class PaymentConfirmationOutbox
{
    public long Id { get; set; }
    [Required, MaxLength(64)]
    public string Provider { get; set; }
    [Required, MaxLength(256)]
    public string ProviderTransactionId { get; set; }
    [Required, MaxLength(32)]
    public string ConfirmationType { get; set; }
    [Required]
    public string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseUntil { get; set; }
    [MaxLength(2000)]
    public string LastError { get; set; }
}
