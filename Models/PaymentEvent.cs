namespace Coflnet.Payments.Models;
public class PaymentEvent
{
    public double PayedAmount { get; set; }
    public string ProductId { get; set; }
    public string UserId { get; set; }
    public Address Address { get; set; }
    public string FullName { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string Currency { get; set; }
    public string PaymentMethod { get; set; }
    public string PaymentProvider { get; set; }
    public string PaymentProviderTransactionId { get; set; }
    public DateTime Timestamp { get; set; }
}
