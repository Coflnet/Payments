using System;
using Coflnet.Payments.Services;
using NUnit.Framework;

namespace Payments.Services;

public class CoinGateServiceTests
{
    /// <summary>
    /// Regression: the live API ignores a paid_at[from] value with a time part (returns all orders);
    /// only yyyy-MM-dd narrows the list. Verified 2026-10-07.
    /// </summary>
    [Test]
    public void PaidOrdersFilterDate_IsDateOnlyUtc()
    {
        var local = new DateTime(2026, 8, 1, 23, 30, 0, DateTimeKind.Utc);
        Assert.That(CoinGateService.PaidOrdersFilterDate(local), Is.EqualTo("2026-08-01"));
        Assert.That(CoinGateService.PaidOrdersFilterDate(new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc)), Is.EqualTo("2026-08-02"));
        Assert.That(CoinGateService.PaidOrdersFilterDate(local), Does.Not.Contain(" "));
    }

    // regression: CoinGate error bodies were logged and put into exception messages, and they can echo the purchaser's email and the callback token
    [Test]
    public void ErrorReasonKeepsOnlyTheReasonAndMessage()
    {
        var body = "{\"message\":\"Order is not valid\",\"reason\":\"OrderIsNotValid\",\"errors\":[\"purchaser_email buyer@example.com is invalid\"],\"token\":\"secret-token\"}";

        var reason = CoinGateService.CoinGateErrorReason(body);

        Assert.That(reason, Is.EqualTo("OrderIsNotValid - Order is not valid"));
        Assert.That(reason, Does.Not.Contain("example.com").And.Not.Contain("secret-token"));
    }

    [TestCase("<html>bad gateway buyer@example.com</html>", "unreadable error")]
    [TestCase("[]", "unreadable error")]
    [TestCase("{}", "no error reason")]
    public void ErrorReasonNeverReturnsTheRawBody(string body, string expected)
    {
        Assert.That(CoinGateService.CoinGateErrorReason(body), Is.EqualTo(expected));
    }
}
