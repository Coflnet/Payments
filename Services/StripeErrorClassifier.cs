using System.Net;

namespace Coflnet.Payments.Services;

public static class StripeErrorClassifier
{
    public const string UserMessage =
        "Stripe payment is temporarily unavailable because its API access is misconfigured. Please contact support.";

    public static bool IsCredentialError(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.Unauthorized
            || statusCode == HttpStatusCode.Forbidden;
    }
}
