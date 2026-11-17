using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Coflnet.Payments.Models.PayPal;

/// <summary>
/// PayPal Subscriptions v1 subscription resource (GET /v1/billing/subscriptions/{id} and webhook resource)
/// </summary>
[DataContract]
public class PayPalSubscription
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; }
    [DataMember(Name = "plan_id")] public string PlanId { get; set; }
    [DataMember(Name = "custom_id")] public string CustomId { get; set; }
    [DataMember(Name = "create_time")] public string CreateTime { get; set; }
    [DataMember(Name = "billing_info")] public PayPalBillingInfo BillingInfo { get; set; }
    [DataMember(Name = "links")] public List<PayPalLink> Links { get; set; }
}

[DataContract]
public class PayPalBillingInfo
{
    [DataMember(Name = "next_billing_time")] public string NextBillingTime { get; set; }
    [DataMember(Name = "last_payment")] public PayPalLastPayment LastPayment { get; set; }
}

[DataContract]
public class PayPalLastPayment
{
    [DataMember(Name = "amount")] public PayPalMoney Amount { get; set; }
    [DataMember(Name = "time")] public string Time { get; set; }
}

[DataContract]
public class PayPalMoney
{
    [DataMember(Name = "currency_code")] public string CurrencyCode { get; set; }
    [DataMember(Name = "value")] public string Value { get; set; }
}

[DataContract]
public class PayPalLink
{
    [DataMember(Name = "href")] public string Href { get; set; }
    [DataMember(Name = "rel")] public string Rel { get; set; }
    [DataMember(Name = "method")] public string Method { get; set; }
}

/// <summary>
/// GET /v1/billing/subscriptions/{id}/transactions
/// </summary>
[DataContract]
public class PayPalTransactionList
{
    [DataMember(Name = "transactions")] public List<PayPalTransaction> Transactions { get; set; }
}

[DataContract]
public class PayPalTransaction
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "status")] public string Status { get; set; }
    [DataMember(Name = "time")] public string Time { get; set; }
    [DataMember(Name = "amount_with_breakdown")] public PayPalAmountWithBreakdown AmountWithBreakdown { get; set; }
}

[DataContract]
public class PayPalAmountWithBreakdown
{
    [DataMember(Name = "gross_amount")] public PayPalMoney GrossAmount { get; set; }
}

/// <summary>
/// v1 sale (PAYMENT.SALE.* webhook resource, GET /v1/payments/sale/{id})
/// </summary>
[DataContract]
public class PayPalSale
{
    [DataMember(Name = "id")] public string Id { get; set; }
    [DataMember(Name = "state")] public string State { get; set; }
    [DataMember(Name = "amount")] public PayPalSaleAmount Amount { get; set; }
    [DataMember(Name = "billing_agreement_id")] public string BillingAgreementId { get; set; }
    [DataMember(Name = "create_time")] public string CreateTime { get; set; }
    /// <summary>Set on refund resources (PAYMENT.SALE.REFUNDED)</summary>
    [DataMember(Name = "sale_id")] public string SaleId { get; set; }
}

[DataContract]
public class PayPalSaleAmount
{
    [DataMember(Name = "total")] public string Total { get; set; }
    [DataMember(Name = "currency")] public string Currency { get; set; }
}
