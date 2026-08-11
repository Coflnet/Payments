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
    public string ConfirmationType { get; set; }
    public decimal? CoinAmount { get; set; }
    public DateTime? ServiceStartsAtUtc { get; set; }
    public DateTime? ServiceEndsAtUtc { get; set; }
    public string DeclarationVersion { get; set; }
    public string DeclarationText { get; set; }
    public string LegalLocale { get; set; }
    public string AgreementId { get; set; }
    public string AgreementHash { get; set; }
    public string WithdrawalVersion { get; set; }
    public string WithdrawalSha256 { get; set; }
}
