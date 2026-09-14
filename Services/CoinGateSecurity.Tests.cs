using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Coflnet.Payments.Models.CoinGate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Payments.Services;

public class CoinGateSecurityTests
{
    [TestCase("valid", true)]
    [TestCase("other-id", false)]
    [TestCase("other-order", false)]
    [TestCase("other-currency", false)]
    [TestCase("empty-secret-token", false)]
    [TestCase("no-secret", false)]
    public async Task CallbackIsBoundToApiOrderAndNonemptySecret(string scenario, bool accepted)
    {
        const string orderId = "CG-user-1-123";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["COINGATE:API_TOKEN"] = scenario == "no-secret" ? "" : "api-secret",
            ["COINGATE:CALLBACK_SECRET"] = ""
        }).Build();
        var service = new FakeCoinGate(config)
        {
            Order = new CoinGateOrderResponse
            {
                Id = scenario == "other-id" ? 2 : 1,
                OrderId = scenario == "other-order" ? "someone-elses-order" : orderId,
                PriceCurrency = scenario == "other-currency" ? "USD" : "EUR",
                Status = "paid", PriceAmount = 10
            }
        };
        var secret = scenario is "empty-secret-token" or "no-secret" ? "" : "api-secret";
        var callback = new CoinGateCallback
        {
            Id = 1, OrderId = orderId, PriceCurrency = "EUR", Status = "paid", PriceAmount = 10,
            Token = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{orderId}:user:1:100:{secret}")))[..32]
        };
        Assert.That(await service.VerifyCallback(callback, "user", 1, 100), Is.EqualTo(accepted));
    }

    private sealed class FakeCoinGate(IConfiguration config) : CoinGateService(config, NullLogger<CoinGateService>.Instance)
    {
        public CoinGateOrderResponse Order { get; set; }
        public override Task<CoinGateOrderResponse> GetOrder(long id) => Task.FromResult(Order);
    }
}
