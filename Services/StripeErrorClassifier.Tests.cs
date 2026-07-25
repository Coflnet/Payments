using System.Net;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class StripeErrorClassifierTests
{
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public void DetectsCredentialAndPermissionErrors(HttpStatusCode statusCode)
    {
        Assert.That(StripeErrorClassifier.IsCredentialError(statusCode), Is.True);
    }

    [TestCase(HttpStatusCode.BadRequest)]
    [TestCase(HttpStatusCode.PaymentRequired)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public void DoesNotMislabelOtherStripeErrors(HttpStatusCode statusCode)
    {
        Assert.That(StripeErrorClassifier.IsCredentialError(statusCode), Is.False);
    }
}
