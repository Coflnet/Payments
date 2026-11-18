using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NUnit.Framework;
using Payments.Controllers;

namespace Coflnet.Payments.Services.Subscriptions;

/// <summary>
/// The 4-week renewal against sky's real product catalog (products.json as applied on 2026-11-18): its groups are uneven
/// (the quarter plans are not in starter_premium, the starter plans only are, no slot bundle is in a group), which must
/// still give every 13/52 week plan its own tier's 4-week plan.
/// </summary>
public class ProductionCatalogRenewalTests
{
    private SqliteConnection connection;
    private DbContextOptions<PaymentContext> options;

    [SetUp]
    public async Task ApplyTheCatalogLikeProduction()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options;
        await using var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        var groups = new GroupService(NullLogger<GroupService>.Instance, db);
        var products = new ProductService(NullLogger<ProductService>.Instance, db, groups);
        // POST /Apply (ProductProvisioner), deserialized as Startup configures MVC: Newtonsoft with string enums
        var state = JsonConvert.DeserializeObject<SystemState>(Catalog, new StringEnumConverter());
        await new ApplyController(db, products, groups, new RuleEngine(NullLogger<RuleEngine>.Instance, db)).ApplyState(state);
    }

    [TearDown]
    public async Task CloseDatabase() => await connection.DisposeAsync();

    [TestCase("l_starter_premium-quarter", 13, 1, "l_starter_premium")]
    [TestCase("l_starter_premium-year", 52, 1, "l_starter_premium")]
    [TestCase("l_premium-quarter", 13, 2, "l_premium")]
    [TestCase("l_premium-year", 52, 2, "l_premium")]
    [TestCase("l_prem_plus-quarter", 13, 3, "l_prem_plus")]
    [TestCase("l_prem_plus-year", 52, 3, "l_prem_plus")]
    public async Task EveryLongPlanContinuesAsTheFourWeekPlanOfItsTier(string slug, int weeks, int tier, string renewal)
    {
        await using var db = new PaymentContext(options);
        // As the checkout loads it: ProductService does not load the groups
        var plan = await new ProductService(NullLogger<ProductService>.Instance, db, null).GetTopupProduct(slug);
        var withGroups = await db.TopUpProducts.Include(p => p.Groups).AsNoTracking().SingleAsync(p => p.Slug == slug);

        Assert.That(SubscriptionService.IsSubscriptionPlan(plan), Is.True);
        Assert.That(SubscriptionService.IntervalWeeks(plan.OwnershipSeconds), Is.EqualTo(weeks));
        Assert.That(SubscriptionRenewal.ContinuesAsFourWeeks(plan), Is.True);
        Assert.That(SubscriptionService.TierRank(withGroups), Is.EqualTo(tier));
        Assert.That((await new SubscriptionRenewalPlans(db).Require(plan)).Slug, Is.EqualTo(renewal));
    }

    [TestCase("l_starter_premium")]
    [TestCase("l_premium")]
    [TestCase("l_prem_plus")]
    [TestCase("l_premium-slots")]
    [TestCase("l_premium-slots-4")]
    [TestCase("l_prem_plus-slots")]
    [TestCase("l_prem_plus-slots-4")]
    public async Task FourWeekPlansAndSlotBundlesRenewWithTheirOwnPeriod(string slug)
    {
        await using var db = new PaymentContext(options);
        var plan = await new ProductService(NullLogger<ProductService>.Instance, db, null).GetTopupProduct(slug);

        Assert.That(SubscriptionService.IsSubscriptionPlan(plan), Is.True);
        Assert.That(SubscriptionService.IntervalWeeks(plan.OwnershipSeconds), Is.EqualTo(4));
        Assert.That(await new SubscriptionRenewalPlans(db).Find(plan), Is.Null);
    }

    /// <summary>
    /// The tier ranks the 4-week plans resolve by: the highest tier group (the 4-week premium plans are also in starter_premium),
    /// the slot bundles by their slot tier (they are in no group)
    /// </summary>
    [Test]
    public async Task FourWeekPlansRankByTheirTier()
    {
        await using var db = new PaymentContext(options);
        var ranks = await db.TopUpProducts.Include(p => p.Groups).AsNoTracking()
            .Where(p => p.Slug.StartsWith("l_")).ToListAsync();
        var fourWeeks = ranks.Where(p => SubscriptionService.IsSubscriptionPlan(p) && SubscriptionService.IntervalWeeks(p.OwnershipSeconds) == 4)
            .ToDictionary(p => p.Slug, SubscriptionService.TierRank);
        Assert.That(fourWeeks, Is.EquivalentTo(new Dictionary<string, int>
        {
            ["l_starter_premium"] = 1, ["l_premium"] = 2, ["l_prem_plus"] = 3, ["l_premium-slots"] = 2, ["l_premium-slots-4"] = 2,
            ["l_prem_plus-slots"] = 3, ["l_prem_plus-slots-4"] = 3,
            ["l_bazaarpro"] = 0 // no tier: no 13/52 week plan continues as it
        }));
    }

    /// <summary>sky's products.json, verbatim</summary>
    private const string Catalog = """
{
"products": [
{
  "slug": "premium-slots",
  "title": "Premium slot",
  "description": "Purchaser-owned capacity; only the purchaser can assign, revoke, extend or refund slots.",
  "cost": 1800,
  "ownershipSeconds": 2592000,
  "type": "SERVICE",
  "slotCount": 1,
  "slotTier": "premium"
},
{
  "slug": "premium-slots-4",
  "title": "Premium 4 slots, 30 days",
  "description": "Four purchaser-owned Premium slots for 30 days; assign each slot to a friend.",
  "cost": 6000,
  "ownershipSeconds": 2592000,
  "type": "SERVICE",
  "slotCount": 4,
  "slotTier": "premium"
},
{
  "slug": "premium_plus-slot-weeks",
  "title": "Premium+ slot, 4 weeks",
  "description": "Purchaser-owned capacity; only the purchaser can assign, revoke, extend or refund slots.",
  "cost": 9000,
  "ownershipSeconds": 2419200,
  "type": "SERVICE",
  "slotCount": 1,
  "slotTier": "premium_plus"
},
{
  "slug": "premium_plus-slots-4",
  "title": "Premium+ 4 slots, 4 weeks",
  "description": "Purchaser-owned capacity; only the purchaser can assign, revoke, extend or refund slots.",
  "cost": 27000,
  "ownershipSeconds": 2419200,
  "type": "SERVICE",
  "slotCount": 4,
  "slotTier": "premium_plus"
},
{
"slug": "advent-calendar",
"title": "Advent calendar",
"description": "Extra perks for the advent calendar",
"cost": 1000,
"ownershipSeconds": 2592000,
"type": "SERVICE"
},
{
"slug": "premium",
"title": "Premium",
"description": "Premium plan granting various perks",
"cost": 1800,
"ownershipSeconds": 2592000,
"type": "SERVICE"
},
{
"slug": "premium-day",
"title": "Premium (day)",
"description": "Premium plan granting various perks for one day",
"cost": 300,
"ownershipSeconds": 86400,
"type": "SERVICE"
},
{
"slug": "premium-derpy",
"title": "Premium (Derpy time)",
"description": "Premium time to compensate for derpy",
"cost": 400,
"ownershipSeconds": 432000,
"type": "SERVICE"
},
{
"slug": "premium_plus-weeks",
"title": "Premium+ 4 weeks",
"description": "Premium Plus plan granting top tier perks for 4 weeks",
"cost": 9000,
"ownershipSeconds": 2419200,
"type": "SERVICE"
},
{
"slug": "premium_plus-months",
"title": "Premium+ 11 weeks",
"description": "Premium Plus plan granting top tier perks for 11 weeks",
"cost": 21600,
"ownershipSeconds": 6652800,
"type": "SERVICE"
},
{
"slug": "premium_plus-week",
"title": "Premium+ 1 week",
"description": "Premium Plus plan granting top tier perks for 1 week",
"cost": 2700,
"ownershipSeconds": 604800,
"type": "SERVICE"
},
{
"slug": "premium_plus",
"title": "Premium+",
"description": "Premium Plus plan granting top tier perks",
"cost": 2700,
"ownershipSeconds": 604800,
"type": "SERVICE"
},
{
"slug": "premium_plus-day",
"title": "Premium+ (day)",
"description": "Premium Plus plan granting top tier perks for a day",
"cost": 600,
"ownershipSeconds": 86400,
"type": "SERVICE"
},
{
"slug": "premium_plus-hour",
"title": "Premium+ (hour)",
"description": "Premium Plus plan granting top tier perks for an hour",
"cost": 200,
"ownershipSeconds": 3600,
"type": "SERVICE"
},
{
"slug": "premium_plus-100",
"title": "Premium+ (100 days)",
"description": "Premium Plus plan granting top tier perks for a 100 days",
"cost": 21600,
"ownershipSeconds": 8640000,
"type": "SERVICE"
},
{
"slug": "starter_premium",
"title": "NotEnoughCoin",
"description": "Access to basic perks",
"cost": 1800,
"ownershipSeconds": 15552000,
"type": "SERVICE"
},
{
"slug": "starter_premium-hour",
"title": "Basic plan (day)",
"description": "Access to basic perks for 1 hour",
"cost": 4,
"ownershipSeconds": 3600,
"type": "SERVICE"
},
{
"slug": "starter_premium-day",
"title": "Basic plan (day)",
"description": "Access to basic perks for 24 hours",
"cost": 24,
"ownershipSeconds": 86400,
"type": "SERVICE"
},
{
"slug": "starter_premium-week",
"title": "Basic plan (week)",
"description": "Access to basic perks for one week",
"cost": 120,
"ownershipSeconds": 604800,
"type": "SERVICE"
},
{
"slug": "test-premium",
"title": "Test Premium for invited user",
"cost": 100,
"type": "SERVICE",
"ownershipSeconds": 259200
},
{
"slug": "pre_api",
"title": "Player api based flips (before main api update)",
"description": "Utilizes the player auctions api endpoint to find auctions before the main auction API endpoint refreshes",
"cost": 1400,
"type": "SERVICE",
"ownershipSeconds": 3600
},
{
"slug": "slowdown",
"title": "Temporarily slow down a user",
"description": "Temporarily slow down a user",
"cost": 1330,
"type": "SERVICE",
"ownershipSeconds": 60
},
{
"slug": "vps",
"title": "Managed vps",
"description": "Managed instance of a vps",
"cost": 2700,
"type": "SERVICE",
"ownershipSeconds": 2592000
},
{
"slug": "vps+",
"title": "Managed vps+",
"description": "Managed instance of a vps with paid software",
"cost": 5100,
"type": "SERVICE",
"ownershipSeconds": 2592000
},
{
"slug": "bazaarpro",
"title": "1 month Bazaar Pro",
"description": "4 weeks of Bazaar Pro",
"cost": 2100,
"ownershipSeconds": 2430000,
"type": "SERVICE"
},
{
"slug": "bazaarpro-trial",
"title": "3 days Bazaar Pro",
"description": "3 days of Bazaar Pro",
"cost": 200,
"ownershipSeconds": 259200,
"type": "SERVICE"
},
{
"slug": "config-purchase",
"title": "Config purchase",
"description": "Represents a purchase of a config",
"cost": 300,
"type": "SERVICE",
"ownershipSeconds": 1
},
{
"slug": "export-unlock",
"title": "Unlock export of some item",
"description": "Unlocks the csv/excel export for a specific item",
"cost": 300,
"type": "SERVICE",
"ownershipSeconds": 1
},
{
"slug": "rust-addon",
"title": "Rust finder addon (discontinued)",
"description": "Unlocks rust finder addon",
"cost": 12000,
"type": "SERVICE",
"ownershipSeconds": 2092000
}
],
"topups": [
{
"slug": "ps_cc_1800",
"title": "1.800 CoflCoins",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "googlepay",
"type": 4
},
{
"slug": "ps_cc_5400",
"title": "5.400 CoflCoins",
"cost": 5400,
"price": 24.69,
"currencyCode": "eur",
"ProviderSlug": "googlepay",
"type": 4
},
{
"slug": "ps_cc_10800",
"title": "10.800 CoflCoins",
"cost": 10800,
"price": 44.69,
"currencyCode": "eur",
"ProviderSlug": "googlepay",
"type": 4
},
{
"slug": "s_cc_1800",
"title": "1.800 CoflCoins",
"cost": 1800,
"price": 9.42,
"currencyCode": "eur",
"ProviderSlug": "stripe",
"type": 4
},
{
"slug": "s_cc_5400",
"title": "5.400 CoflCoins",
"cost": 5400,
"price": 22.69,
"currencyCode": "eur",
"ProviderSlug": "stripe",
"type": 4
},
{
"slug": "s_cc_10800",
"title": "10.800 CoflCoins",
"cost": 10800,
"price": 38.99,
"currencyCode": "eur",
"ProviderSlug": "stripe",
"type": 4
},
{
"slug": "s_cc_21600",
"title": "21.600 CoflCoins",
"cost": 21600,
"price": 74.99,
"currencyCode": "eur",
"ProviderSlug": "stripe",
"type": 4
},
{
"slug": "p_cc_1800",
"title": "1.800 CoflCoins",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "paypal",
"type": 4
},
{
"slug": "p_cc_5400",
"title": "5.400 CoflCoins",
"cost": 5400,
"price": 22.99,
"currencyCode": "eur",
"ProviderSlug": "paypal",
"type": 4
},
{
"slug": "p_cc_10800",
"title": "10.800 CoflCoins",
"cost": 10800,
"price": 39.69,
"currencyCode": "eur",
"ProviderSlug": "paypal",
"type": 4
},
{
"slug": "p_cc_21600",
"title": "21.600 CoflCoins",
"cost": 21600,
"price": 78.69,
"currencyCode": "eur",
"ProviderSlug": "paypal",
"type": 4
},
{
"slug": "c_cc_1800",
"title": "1.800 CoflCoins",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "coingate",
"type": 4
},
{
"slug": "c_cc_5400",
"title": "5.400 CoflCoins",
"cost": 5400,
"price": 22.99,
"currencyCode": "eur",
"ProviderSlug": "coingate",
"type": 4
},
{
"slug": "c_cc_10800",
"title": "10.800 CoflCoins",
"cost": 10800,
"price": 39.69,
"currencyCode": "eur",
"ProviderSlug": "coingate",
"type": 4
},
{
"slug": "c_cc_21600",
"title": "21.600 CoflCoins",
"cost": 21600,
"price": 78.69,
"currencyCode": "eur",
"ProviderSlug": "coingate",
"type": 4
},
{
"slug": "l_bazaarpro",
"title": "1 month Bazaar Pro",
"description": "4 weeks of Bazaar Pro renewing itself",
"cost": 2100,
"price": 12.99,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2430000,
"type": "SERVICE"
},
{
"slug": "l_bazaarpro-year",
"title": "1 year Bazaar Pro",
"description": "Full year of Bazaar Pro renewing itself",
"cost": 21000,
"price": 69.99,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 31536000,
"type": "SERVICE"
},
{
"slug": "l_bazaarpro-quarter",
"title": "3 months Bazaar Pro",
"description": "3 months of Bazaar Pro renewing itself",
"cost": 6300,
"price": 38.97,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 7776000,
"type": "SERVICE"
},
{
"slug": "l_prem_plus-slots-4",
"title": "Premium+ 4 slots, 4 weeks",
"description": "Four purchaser-owned Premium+ slots renewing every 4 weeks; assignments are preserved on renewal.",
"cost": 27000,
"price": 99.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2419200,
"type": "SERVICE",
"slotCount": 4,
"slotTier": "premium_plus"
},
{
"slug": "l_prem_plus-slots",
"title": "Premium+ single slot, 4 weeks",
"description": "One purchaser-owned Premium+ slot at the normal subscription price. Assign or cancel this slot subscription from Account > Purchased slots; access continues through the paid period.",
"cost": 8500,
"price": 35.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2419200,
"type": "SERVICE",
"slotCount": 1,
"slotTier": "premium_plus"
},
{
"slug": "l_premium-slots",
"title": "Premium single slot, 4 weeks",
"description": "One purchaser-owned Premium slot at the normal subscription price. Assign or cancel this slot subscription from Account > Purchased slots; access continues through the paid period.",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2419200,
"type": "SERVICE",
"slotCount": 1,
"slotTier": "premium"
},
{
"slug": "l_premium-slots-4",
"title": "Premium 4 slots, 4 weeks",
"description": "Four purchaser-owned Premium slots renewing every 4 weeks; assignments are preserved on renewal.",
"cost": 7200,
"price": 29.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2419200,
"type": "SERVICE",
"slotCount": 4,
"slotTier": "premium"
},
{
"slug": "l_prem_plus-quarter",
"title": "3 months Prem+",
"description": "3 months of Premium+ renewing itself",
"cost": 25500,
"price": 99.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 7776000,
"type": "SERVICE"
},
{
"slug": "l_premium-quarter",
"title": "3 months Premium",
"description": "3 months of Premium renewing itself",
"cost": 5400,
"price": 27.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 7776000,
"type": "SERVICE"
},
{
"slug": "l_starter_premium-quarter",
"title": "3 months Basic plan",
"description": "3 months of the basic tier renewing itself",
"cost": 1350,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 7776000,
"type": "SERVICE"
},
{
"slug": "l_prem_plus",
"title": "4 weeks prem+",
"description": "4 weeks of premium+ renewing itself",
"cost": 8500,
"price": 35.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2430000,
"type": "SERVICE"
},
{
"slug": "l_prem_plus-year",
"title": "1 year Prem+",
"description": "1 YEAR of premium+ renewing itself",
"cost": 84000,
"price": 354.20,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 31536000,
"type": "SERVICE"
},
{
"slug": "l_starter_premium-year",
"title": "1 year Basic plan",
"description": "1 YEAR our basic tier",
"cost": 3600,
"price": 16.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 31536000,
"type": "SERVICE"
},
{
"slug": "l_starter_premium",
"title": "1 month Basic plan",
"description": "1 month our basic tier",
"cost": 450,
"price": 2.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2430000,
"type": "SERVICE"
},
{
"slug": "l_premium",
"title": "4 weeks premium",
"description": "4 weeks of premium renewing itself",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 2430000,
"type": "SERVICE"
},
{
"slug": "l_premium-year",
"title": "1 year Premium",
"description": "1 YEAR of premium renewing itself",
"cost": 20000,
"price": 96.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"ownershipSeconds": 31536000,
"type": "SERVICE"
},
{
"slug": "l_cc_1800",
"title": "1.800 CoflCoins",
"cost": 1800,
"price": 9.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"type": 4
},
{
"slug": "l_cc_5400",
"title": "5.400 CoflCoins",
"cost": 5400,
"price": 22.99,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"type": 4
},
{
"slug": "l_cc_10800",
"title": "10.800 CoflCoins",
"cost": 10800,
"price": 39.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"type": 4
},
{
"slug": "l_cc_21600",
"title": "21.600 CoflCoins",
"cost": 21600,
"price": 78.69,
"currencyCode": "eur",
"ProviderSlug": "lemonsqueezy",
"type": 4
},
{
"slug": "verify_mc",
"title": "Bonus for the MC verification",
"cost": 100,
"ProviderSlug": "custom",
"type": 4
},
{
"slug": "referal_bonus",
"title": "Bonus if a refered user purchased premium",
"cost": 5401,
"ProviderSlug": "custom",
"type": 36
},
{
"slug": "compensation",
"title": "Compensation for service unavailibility",
"cost": 10800,
"ProviderSlug": "custom",
"type": 36
},
{
"slug": "advent-reward",
"title": "Advent calendar reward",
"cost": 100,
"ProviderSlug": "custom",
"type": 36
},
{
"slug": "tfm-transfer",
"title": "Balance imported from tfm",
"cost": 300000,
"ProviderSlug": "custom",
"type": 36
},
{
"slug": "config-sell",
"title": "Sold a config to somebody",
"cost": 15120,
"ProviderSlug": "custom",
"type": 36
}
],
"groups": {
"premium": [
"test-premium",
"premium-day",
"premium_plus",
"premium_plus-day",
"premium_plus-hour",
"premium_plus-100",
"premium-derpy",
"l_prem_plus",
"l_premium",
"l_premium-quarter",
"l_premium-year",
"l_prem_plus-quarter",
"l_prem_plus-year",
"premium_plus-week",
"premium_plus-weeks",
"premium_plus-months"
],
"starter_premium": [
"premium",
"premium-day",
"premium_plus",
"premium_plus-day",
"premium_plus-hour",
"premium_plus-100",
"starter_premium-hour",
"starter_premium-day",
"starter_premium-week",
"l_prem_plus",
"l_premium",
"l_premium-year",
"l_prem_plus-year",
"l_starter_premium-year",
"l_starter_premium",
"l_starter_premium-quarter",
"premium_plus-week",
"premium_plus-weeks",
"premium_plus-months"
],
"premium_plus": [
"premium_plus-day",
"premium_plus-hour",
"premium_plus-weeks",
"premium_plus-months",
"premium_plus-week",
"premium_plus-100",
"pre_api",
"l_prem_plus-year",
"l_prem_plus",
"l_prem_plus-quarter"
],
"l_bazaarpro": [
"l_bazaarpro-year",
"bazaarpro",
"l_bazaarpro-quarter",
"bazaarpro-trial"
]
},
"rules": [
{
"flags": 10,
"slug": "premplus-preapi-discount",
"priority": 5,
"requiresGroup": "premium_plus",
"targetsGroup": "pre_api",
"amount": 30.0
}
]
}
""";
}
