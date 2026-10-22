using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PayPalCheckoutSdk.Core;
using PayPalCheckoutSdk.Orders;

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

    /// <summary>
    /// The callback only fulfils PayPal orders that match a request recorded at creation time,
    /// so creating an order must store the exact amount and product sent to PayPal.
    /// </summary>
    [Test]
    public async Task PayPal_RecordsServerPricedRequestForTheCreatedOrder()
    {
        await using var fixture = await Fixture.Create("DE");

        var response = await fixture.Controller.CreatePayPal(
            "207906",
            "p_cc_1800",
            new TopUpOptions { TopUpAmount = 21600, UserIp = "8.8.8.8", Locale = "de-DE" });

        Assert.That(response.Id, Is.EqualTo("ORDER-1"));
        var unit = fixture.PayPal.CreatedOrder.PurchaseUnits[0];
        Assert.That(unit.CustomId, Is.EqualTo("155;21600;207906"));
        Assert.That(unit.AmountWithBreakdown.Value, Is.EqualTo("116.28"));
        var request = await fixture.Context.PaymentRequests.Include(r => r.User).Include(r => r.ProductId).SingleAsync();
        Assert.That(request.Provider, Is.EqualTo("paypal"));
        Assert.That(request.SessionId, Is.EqualTo("ORDER-1"));
        Assert.That(request.Amount, Is.EqualTo(116.28m));
        Assert.That(request.ProductId.Id, Is.EqualTo(155));
        Assert.That(request.User.ExternalId, Is.EqualTo("207906"));
        Assert.That(request.State, Is.EqualTo(PaymentRequest.Status.CREATED));
        Assert.That(request.CreateOnIp, Is.EqualTo(IPAddress.Parse("8.8.8.8")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public PaymentContext Context { get; }
        public TopUpController Controller { get; }
        public StubCoinGateService CoinGate { get; }
        public FakePayPalOrders PayPal { get; } = new();

        private Fixture(
            SqliteConnection connection,
            PaymentContext context,
            TopUpController controller,
            StubCoinGateService coinGate,
            FakePayPalOrders payPal = null)
        {
            _connection = connection;
            Context = context;
            Controller = controller;
            CoinGate = coinGate;
            PayPal = payPal ?? new FakePayPalOrders();
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
            context.TopUpProducts.Add(new TopUpProduct
            {
                Id = 155,
                Title = "1,800 CoflCoins",
                Slug = "p_cc_1800",
                Cost = 1800,
                Price = 9.69m,
                CurrencyCode = "EUR",
                ProviderSlug = "paypal",
                Type = Product.ProductType.TOP_UP
            });
            await context.SaveChangesAsync();

            var config = new ConfigurationBuilder().Build();
            var coinGate = new StubCoinGateService(config);
            var userService = new UserService(NullLogger<UserService>.Instance, context);
            var groupService = new GroupService(NullLogger<GroupService>.Instance, context);
            var productService = new ProductService(NullLogger<ProductService>.Instance, context, groupService);
            var fixture = new Fixture(connection, context, null, coinGate);
            var controller = new TopUpController(
                NullLogger<TopUpController>.Instance,
                context,
                productService,
                null,
                config,
                userService,
                fixture.PayPal,
                null,
                null,
                coinGate,
                new StubIpCountryLookup(ipCountry));

            return new Fixture(connection, context, controller, coinGate, fixture.PayPal);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakePayPalOrders : PayPalHttpClient
    {
        public OrderRequest CreatedOrder { get; private set; }

        public FakePayPalOrders() : base(new SandboxEnvironment("test", "test")) { }

        public override Task<PayPalHttp.HttpResponse> Execute<T>(T request)
        {
            if (request is not OrdersCreateRequest create)
                throw new HttpRequestException("Unexpected PayPal call: " + request.Path);
            CreatedOrder = (OrderRequest)create.Body;
            var reply = JObject.Parse("""
            {"id":"ORDER-1","status":"CREATED","intent":"CAPTURE",
             "links":[{"rel":"approve","href":"https://sandbox.paypal.invalid/approve/ORDER-1","method":"GET"}]}
            """);
            using var content = new StringContent(reply.ToString(), Encoding.UTF8, "application/json");
            var result = Encoder.DeserializeResponse(content, request.ResponseType);
            return Task.FromResult(new PayPalHttp.HttpResponse(content.Headers, HttpStatusCode.Created, result));
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
