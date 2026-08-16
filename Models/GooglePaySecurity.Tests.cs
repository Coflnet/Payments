using Coflnet.Payments.Models.GooglePay;
using NUnit.Framework;

public class GooglePaySecurityTests
{
    [Test]
    public void PurchaseRequestHasNoCallerControlledAmount()
    {
        Assert.That(
            typeof(GooglePlayPurchaseRequest).GetProperty("CustomAmount"),
            Is.Null);
    }
}
