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
}
