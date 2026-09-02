using System.ComponentModel.DataAnnotations;

namespace Coflnet.Payments.Models;

/// <summary>
/// Order-specific evidence for paid digital content or a service supplied
/// during the withdrawal period.
/// </summary>
public class ServicePerformanceDeclaration
{
    public long Id { get; set; }
    [Required, MaxLength(64)]
    public string RequestId { get; set; }
    [Required, MaxLength(128)]
    public string UserId { get; set; }
    public int ProductId { get; set; }
    [Required, MaxLength(128)]
    public string ProductSlug { get; set; }
    public int Count { get; set; }
    [Required, MaxLength(80)]
    public string PurchaseReference { get; set; }
    public decimal CoinAmount { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public bool DeclarationRequired { get; set; }
    public bool EarlyPerformanceRequested { get; set; }
    public bool WithdrawalConsequenceAcknowledged { get; set; }
    [Required, MaxLength(2)]
    public string Locale { get; set; }
    [MaxLength(64)]
    public string DeclarationVersion { get; set; }
    [MaxLength(2048)]
    public string DeclarationText { get; set; }
    [MaxLength(64)]
    public string DeclarationSha256 { get; set; }
    [Required, MaxLength(64)]
    public string AgreementId { get; set; }
    [Required, MaxLength(64)]
    public string AgreementHash { get; set; }
    [Required, MaxLength(64)]
    public string WithdrawalVersion { get; set; }
    [Required, MaxLength(64)]
    public string WithdrawalSha256 { get; set; }
    [MaxLength(2)]
    public string TaxCountry { get; set; }
    public int VatRateBasisPoints { get; set; }
    public long GrossEurCents { get; set; }
    public long VatEurCents { get; set; }
    public string OrderDetailsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// A service purchase plus the declaration displayed for that order.
/// </summary>
public sealed class ServicePurchaseRequest
{
    [Required, MaxLength(80)]
    public string Reference { get; set; }
    public int Count { get; set; } = 1;
    public bool ImmediatePerformanceRequested { get; set; }
    public bool WithdrawalConsequenceAcknowledged { get; set; }
    [Required, MaxLength(5)]
    public string Locale { get; set; }
    [MaxLength(64)]
    public string DeclarationVersion { get; set; }
    [MaxLength(2048)]
    public string DeclarationText { get; set; }
    [MaxLength(64)]
    public string DeclarationSha256 { get; set; }
    [MaxLength(64)]
    public string AgreementId { get; set; }
    [MaxLength(64)]
    public string AgreementHash { get; set; }
    [MaxLength(64)]
    public string WithdrawalVersion { get; set; }
    [MaxLength(64)]
    public string WithdrawalSha256 { get; set; }
    [MaxLength(2)]
    public string TaxCountry { get; set; }
    [MaxLength(2)]
    public string ConsumerRightsRegime { get; set; }
    public int VatRateBasisPoints { get; set; }
    public long GrossEurCents { get; set; }
    public long VatEurCents { get; set; }
    public string OrderDetailsJson { get; set; }
    [Required, MaxLength(64)]
    public string RequestId { get; set; }
}

public sealed class ServicePurchaseQuote
{
    public decimal CoinAmount { get; set; }
    public string TaxCountry { get; set; }
    public string ConsumerRightsRegime { get; set; }
    public int VatRateBasisPoints { get; set; }
    public long GrossEurCents { get; set; }
    public long VatEurCents { get; set; }
}
