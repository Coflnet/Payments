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
    public string TaxCountry { get; set; }
    public string ConsumerRightsRegime { get; set; }
    public int VatRateBasisPoints { get; set; }
    public long GrossEurCents { get; set; }
    public long VatEurCents { get; set; }
    public string OrderDetailsJson { get; set; }
    /// <summary>Direct/Managed Payments subscription payments: the term this payment paid for in weeks (4, 13 or 52; 4 once a 13/52 week subscription continues every 4 weeks)</summary>
    public int? SubscriptionBillingWeeks { get; set; }
    /// <summary>Subscription payments: the length of the following cycles in weeks (4 for the 4-week continuation; a legacy plan's own weeks when it repeats its term)</summary>
    public int? SubscriptionRenewalWeeks { get; set; }
    /// <summary>
    /// Subscription payments: what each following cycle charges in <see cref="Currency"/> (direct: incl. VAT; Managed Payments: net,
    /// Stripe adds the tax). Null when the payment was in another currency than the agreed price (Managed Payments' Adaptive Pricing)
    /// </summary>
    public decimal? SubscriptionRenewalAmount { get; set; }
    /// <summary>Subscription payments: false for the first payment of the subscription, true for every later one</summary>
    public bool? SubscriptionIsRenewal { get; set; }
}
