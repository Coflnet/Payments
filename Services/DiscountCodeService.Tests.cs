using System;
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

    [Test]
    public async Task AutoApplyIncludesTheStartAndExcludesTheExpiration()
    {
        var code = BlackFriday();
        var start = new DateTime(2000, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        var expiration = start.AddDays(1);
        code.StartsAt = start;
        code.ExpiresAt = expiration;
        await Codes().Upsert(code);

        var codes = Codes();
        Assert.That(await codes.GetAutoApplyAt(start.AddTicks(-1)), Is.Null);
        Assert.That((await codes.GetAutoApplyAt(start))?.Code, Is.EqualTo("BF2026"));
        Assert.That((await codes.GetAutoApplyAt(expiration.AddTicks(-1)))?.Code, Is.EqualTo("BF2026"));
        Assert.That(await codes.GetAutoApplyAt(expiration), Is.Null);
        Assert.That(await codes.GetActiveAutoApply(), Is.Null);
    }

    [Test]
    public async Task AutoApplyRequiresAnActiveSubscriptionOfferWithRedemptionsLeft()
    {
        var at = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        db.DiscountCodes.AddRange(
            new DiscountCode { Code = "INACTIVE", AutoApply = true, SubscriptionOnly = true, IsActive = false },
            new DiscountCode { Code = "MANUAL", SubscriptionOnly = true },
            new DiscountCode { Code = "ONE-TIME", AutoApply = true },
            new DiscountCode { Code = "USED-UP", AutoApply = true, SubscriptionOnly = true, MaxRedemptions = 1, TimesRedeemed = 1 });
        await db.SaveChangesAsync();

        Assert.That(await Codes().GetAutoApplyAt(at), Is.Null);
    }

    [Test]
    public async Task AutoApplyPrefersNewestCreationTimeThenHighestId()
    {
        var at = new DateTime(2026, 11, 27, 0, 0, 0, DateTimeKind.Utc);
        db.DiscountCodes.AddRange(
            new DiscountCode { Id = 3, Code = "OLDER", AutoApply = true, SubscriptionOnly = true, CreatedAt = at.AddDays(-2) },
            new DiscountCode { Id = 1, Code = "LOWER-ID", AutoApply = true, SubscriptionOnly = true, CreatedAt = at.AddDays(-1) },
            new DiscountCode { Id = 2, Code = "WINNER", AutoApply = true, SubscriptionOnly = true, CreatedAt = at.AddDays(-1), MaxRedemptions = 2, TimesRedeemed = 1 });
        await db.SaveChangesAsync();

        Assert.That((await Codes().GetAutoApplyAt(at))?.Code, Is.EqualTo("WINNER"));
        Assert.That((await Codes().GetActiveAutoApply())?.Code, Is.EqualTo("WINNER"));
    }
}
