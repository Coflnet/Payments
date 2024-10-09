namespace Coflnet.Payments.Models;

public class UserSubscription
{
    public int Id { get; set; }
    public User User { get; set; }
    public Product Product { get; set; }
    public string ProviderSlug { get; set; }
    public string ExternalId { get; set; }
    public string ExternalCustomerId { get; set; }
    public DateTime RenewsAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string PaymentAmount { get; set; }
    public string Status { get; set; }
}