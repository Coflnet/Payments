using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class DiscountCodeServiceTests : DirectSubscriptionTestBase
{
    private DiscountCodeService Codes() => new(db, NullLogger<DiscountCodeService>.Instance);

    private static DiscountCode BlackFriday() => new()
    {
        Code = "bf2026", Name = "Black Friday", AmountType = "percent", Amount = 25, SubscriptionOnly = true, AutoApply = true,
        PeriodAmounts = new() { [4] = 25, [13] = 50, [52] = 50 }
    };

    [Test]
    public async Task PeriodAmountsAndAutoApplySurviveTheDatabaseRoundTrip()
    {
        await Codes().Upsert(BlackFriday());
        db.ChangeTracker.Clear();

        var stored = await db.DiscountCodes.SingleAsync();
        Assert.That(stored.Code, Is.EqualTo("BF2026"));
        Assert.That(stored.AutoApply, Is.True);
        Assert.That(stored.PeriodAmounts, Is.EqualTo(new Dictionary<int, decimal> { [4] = 25, [13] = 50, [52] = 50 }));
        Assert.That(stored.AmountForWeeks(13), Is.EqualTo(50));
        Assert.That(stored.AmountForWeeks(null), Is.EqualTo(25));
    }

    [Test]
    public async Task ChangingAPeriodAmountOfAStoredCodeIsPersisted()
    {
        await Codes().Upsert(BlackFriday());
        var update = BlackFriday();
        update.PeriodAmounts = new() { [13] = 40 };
        await Codes().Upsert(update);
        db.ChangeTracker.Clear();
        Assert.That((await db.DiscountCodes.SingleAsync()).PeriodAmounts, Is.EqualTo(new Dictionary<int, decimal> { [13] = 40 }));
    }

    [Test]
    public async Task CodeWithoutPeriodAmountsStoresNull()
    {
        var code = BlackFriday();
        code.PeriodAmounts = new();
        await Codes().Upsert(code);
        db.ChangeTracker.Clear();
        Assert.That((await db.DiscountCodes.SingleAsync()).PeriodAmounts, Is.Null);
    }

    [TestCase(7, 10)]
    [TestCase(13, 0)]
    [TestCase(13, 100)]
    public void InvalidPeriodAmountsAreRejected(int weeks, decimal amount)
    {
        var code = BlackFriday();
        code.PeriodAmounts = new() { [weeks] = amount };
        Assert.ThrowsAsync<ApiException>(() => Codes().Upsert(code));
    }

    [Test]
    public void AutoApplyNeedsASubscriptionOnlyCode()
    {
        var code = BlackFriday();
        code.SubscriptionOnly = false;
        Assert.ThrowsAsync<ApiException>(() => Codes().Upsert(code));
    }
}
