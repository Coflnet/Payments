namespace Coflnet.Payments.Models;
public class PaymentEvent
{
    public double PayedAmount { get; set; }
    public string ProductId { get; set; }
    /// <summary>Human readable title of the product (additive; null on events from older producers)</summary>
    public string ProductTitle { get; set; }
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
    /// <summary>The VAT country; also set on direct subscription payments (the checkout country whose VAT the price contains)</summary>
    public string TaxCountry { get; set; }
    public string ConsumerRightsRegime { get; set; }
    /// <summary>VAT rate of <see cref="TaxCountry"/>; also set on direct subscription payments (0: no VAT, e.g. the US)</summary>
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
    /// <summary>
    /// How tax relates to <see cref="PayedAmount"/>, one of <see cref="PaymentTaxTreatment"/>; null when the producer does not
    /// know it (older events, one-time purchases)
    /// </summary>
    public string TaxTreatment { get; set; }
    /// <summary>The tax contained in <see cref="PayedAmount"/> in <see cref="Currency"/> (only with <see cref="PaymentTaxTreatment.Included"/>)</summary>
    public decimal? TaxAmount { get; set; }
    /// <summary>How tax relates to <see cref="SubscriptionRenewalAmount"/>, one of <see cref="PaymentTaxTreatment"/>; null without that amount</summary>
    public string RenewalTaxTreatment { get; set; }
    /// <summary>
    /// Renewal reminders (<see cref="PaymentConfirmationTypes.SubscriptionRenewalReminder"/>): the end of the current 13 or 52 week
    /// term (UTC), after which the subscription continues every <see cref="SubscriptionRenewalWeeks"/> weeks
    /// </summary>
    public DateTime? SubscriptionTermEndsAt { get; set; }
    /// <summary>
    /// Renewal reminders: what one more term of the same length costs in <see cref="Currency"/> if the buyer opts in, with the tax
    /// treatment of <see cref="RenewalTaxTreatment"/>; null when the subscription can not be extended
    /// </summary>
    public decimal? SubscriptionExtensionAmount { get; set; }
    /// <summary>Direct subscription events: the provider's subscription id (the website links to the subscription with it)</summary>
    public string SubscriptionExternalId { get; set; }
    /// <summary>Subscription payments: true on the first payment of a term the buyer opted in to extend; null otherwise</summary>
    public bool? SubscriptionIsExtension { get; set; }
    /// <summary>
    /// Subscription payments: true on the payment of a switch to a longer term (charged at once; the paid days left of the previous
    /// term are kept on top of the new one, nothing is refunded); null otherwise. <see cref="ServiceEndsAtUtc"/> is then the new access end.
    /// </summary>
    public bool? SubscriptionIsTermSwitch { get; set; }
    /// <summary>Term switches: the length of the previous term in weeks (4 or 13)</summary>
    public int? SubscriptionPreviousBillingWeeks { get; set; }
    /// <summary>Term switches: when the access paid before the switch ended; the new term is added from there</summary>
    public DateTime? SubscriptionPreviousAccessEndsAt { get; set; }
    /// <summary>Term switches: when the first <see cref="SubscriptionRenewalWeeks"/>-week cycle after the new term is charged</summary>
    public DateTime? SubscriptionNextBillingAt { get; set; }
    /// <summary>Term switches: the automatic promotion that lowered this payment, null without one</summary>
    public string SubscriptionPromotionCode { get; set; }
}

/// <summary>Values of <see cref="PaymentEvent.ConfirmationType"/> this service sets for subscriptions</summary>
public static class PaymentConfirmationTypes
{
    /// <summary>
    /// Not a payment: a 13 or 52 week term ends within 35 days and then continues every 4 weeks (<see cref="PaymentEvent.PayedAmount"/> 0)
    /// </summary>
    public const string SubscriptionRenewalReminder = "subscription_renewal_reminder";
}

/// <summary>How tax relates to an amount of a <see cref="PaymentEvent"/> (purchase confirmations state it next to the amount)</summary>
public static class PaymentTaxTreatment
{
    /// <summary>
    /// The amount includes the tax: direct sales the VAT of <see cref="PaymentEvent.TaxCountry"/>; a Managed Payments payment
    /// is the whole charge including any tax Stripe added (<see cref="PaymentEvent.TaxAmount"/> only when Stripe reports it)
    /// </summary>
    public const string Included = "included";
    /// <summary>The amount is net; the merchant of record (Stripe Managed Payments) adds the tax (only renewal prices)</summary>
    public const string Net = "net";
    /// <summary>No tax is charged on the amount (direct sales to countries without VAT, e.g. the US)</summary>
    public const string None = "none";
}
