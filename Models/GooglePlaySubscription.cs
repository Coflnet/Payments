using Newtonsoft.Json;

namespace Coflnet.Payments.Models.GooglePay;

/// <summary>Whether the user can start the requested Play plan and what to pass to Billing.</summary>
public record GooglePlayEligibility(bool Eligible, string Reason, string PlayProductId, string BasePlanId, string BlockingProvider,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)] string OfferId = null,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)] int? DiscountPercent = null);

/// <summary>The verified purchase state and whether a paid order was granted.</summary>
public record GooglePlaySubscriptionResult(bool IsValid, string Status, string ErrorMessage);
