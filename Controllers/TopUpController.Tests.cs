using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Payments.Controllers;

public class TopUpControllerTests
{
    [Test]
    public async Task CoinGate_SelectedCountryDoesNotMatchIp_ReturnsClearError()
    {
        await using var fixture = await Fixture.Create("US");

        var exception = Assert.ThrowsAsync<ApiException>(() => fixture.Controller.CreateCoinGate(
            "207906",
            "c_cc_1800",
            new TopUpOptions { Country = "DE", UserIp = "8.8.8.8" }));

        Assert.That(
            exception.Message,
            Is.EqualTo("Your selected country (DE) does not match your IP country (US). Crypto payments require both countries to match."));
        Assert.That(fixture.CoinGate.OrderCreated, Is.False);
    }

    [Test]
    public async Task CoinGate_SelectedCountryMatchesIp_PersistsCountryAndCreatesOrder()
    {
        await using var fixture = await Fixture.Create("DE");

        await fixture.Controller.CreateCoinGate(
            "207906",
            "c_cc_1800",
            new TopUpOptions { Country = "de", UserIp = "8.8.8.8" });

        Assert.That(fixture.CoinGate.OrderCreated, Is.True);
        Assert.That((await fixture.Context.Users.SingleAsync()).Country, Is.EqualTo("DE"));
    }

    [Test]
    public async Task CoinGate_LocaleWithoutExplicitCountry_FailsClosed()
    {
        await using var fixture = await Fixture.Create("DE");

        var exception = Assert.ThrowsAsync<ApiException>(() => fixture.Controller.CreateCoinGate(
            "207906",
            "c_cc_1800",
            new TopUpOptions { Locale = "de-DE", UserIp = "8.8.8.8" }));

        Assert.That(exception.Message, Is.EqualTo("Please select your country before using crypto payments."));
        Assert.That(fixture.CoinGate.OrderCreated, Is.False);
    }

    [Test]
    public async Task Stripe_UsesLocaleCountryWhenExplicitCountryIsMissing()
    {
        await using var fixture = await Fixture.Create("US");

        var exception = Assert.ThrowsAsync<ApiException>(() => fixture.Controller.CreateStripeSession(
            "207906",
            "s_cc_1800",
            new TopUpOptions { Locale = "de-DE", UserIp = "8.8.8.8" }));

        Assert.That(
            exception.Message,
            Is.EqualTo("Your selected country (DE) does not match your IP country (US). Stripe payments require both countries to match."));
    }

    [Test]
    public async Task Stripe_ExplicitCountryTakesPrecedenceOverLocale()
    {
        await using var fixture = await Fixture.Create("US");

        var exception = Assert.ThrowsAsync<ApiException>(() => fixture.Controller.CreateStripeSession(
            "207906",
            "s_cc_1800",
            new TopUpOptions { Country = "DE", Locale = "en-US", UserIp = "8.8.8.8" }));

        Assert.That(
            exception.Message,
            Is.EqualTo("Your selected country (DE) does not match your IP country (US). Stripe payments require both countries to match."));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public PaymentContext Context { get; }
        public TopUpController Controller { get; }
        public StubCoinGateService CoinGate { get; }

        private Fixture(
            SqliteConnection connection,
            PaymentContext context,
            TopUpController controller,
            StubCoinGateService coinGate)
        {
            _connection = connection;
            Context = context;
            Controller = controller;
            CoinGate = coinGate;
        }

        public static async Task<Fixture> Create(string ipCountry)
        {
            var connection = new SqliteConnection("Filename=:memory:");
            connection.Open();
            var context = new PaymentContext(
                new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            context.TopUpProducts.Add(new TopUpProduct
            {
                Id = 153,
                Title = "1,800 CoflCoins",
                Slug = "c_cc_1800",
                Cost = 1800,
                Price = 9.69m,
                CurrencyCode = "EUR",
                ProviderSlug = "coingate",
                Type = Product.ProductType.TOP_UP
            });
            context.TopUpProducts.Add(new TopUpProduct
            {
                Id = 154,
                Title = "1,800 CoflCoins",
                Slug = "s_cc_1800",
                Cost = 1800,
                Price = 9.69m,
                CurrencyCode = "EUR",
                ProviderSlug = "stripe",
                Type = Product.ProductType.TOP_UP
            });
            await context.SaveChangesAsync();

            var config = new ConfigurationBuilder().Build();
            var coinGate = new StubCoinGateService(config);
            var userService = new UserService(NullLogger<UserService>.Instance, context);
            var groupService = new GroupService(NullLogger<GroupService>.Instance, context);
            var productService = new ProductService(NullLogger<ProductService>.Instance, context, groupService);
            var controller = new TopUpController(
                NullLogger<TopUpController>.Instance,
                context,
                productService,
                null,
                config,
                userService,
                null,
                null,
                null,
                coinGate,
                new StubIpCountryLookup(ipCountry));

            return new Fixture(connection, context, controller, coinGate);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubIpCountryLookup : IIpCountryLookup
    {
        private readonly string _country;

        public StubIpCountryLookup(string country)
        {
            _country = country;
        }

        public Task<string> GetCountry(string ip) => Task.FromResult(_country);
    }

    private sealed class StubCoinGateService : CoinGateService
    {
        public bool OrderCreated { get; private set; }

        public StubCoinGateService(IConfiguration config)
            : base(config, NullLogger<CoinGateService>.Instance)
        {
        }

        public override Task<TopUpIdResponse> CreateOrder(
            User user,
            TopUpProduct product,
            decimal eurPrice,
            decimal coinAmount,
            TopUpOptions options = null)
        {
            OrderCreated = true;
            return Task.FromResult(new TopUpIdResponse { Id = "test-order" });
        }
    }
}
