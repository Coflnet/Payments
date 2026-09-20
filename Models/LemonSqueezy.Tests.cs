using System.Text.Json;
using NUnit.Framework;

namespace Coflnet.Payments.Models.LemonSqueezy;

public class LemonSqueezyWebhookTests
{
    [Test]
    public void VariantDiscovery_DeserializesNullIntervalsAlongsideSubscriptions()
    {
        const string json = """
            {"data":[
              {"id":"one-off","attributes":{"is_subscription":false,"interval":null,"interval_count":null,"trial_interval_count":null}},
              {"id":"bazaar","attributes":{"is_subscription":true,"interval":"week","interval_count":4,"price":1299,"has_free_trial":true,"trial_interval_count":3}}
            ]}
            """;

        var variants = JsonSerializer.Deserialize<VariantListResponse>(json).Data;

        Assert.That(variants[0].Attributes.IntervalCount, Is.Null);
        Assert.That(variants[0].Attributes.TrialIntervalCount, Is.Null);
        Assert.That(variants[1].Attributes.IntervalCount, Is.EqualTo(4));
        Assert.That(variants[1].Attributes.Price, Is.EqualTo(1299));
    }

    [Test]
    public void OrderRefunded_DeserializesCumulativePartialRefundAmount()
    {
        const string json = """
            {
              "meta": {
                "event_name": "order_refunded",
                "custom_data": {
                  "user_id": "27694",
                  "product_id": "113",
                  "coin_amount": "21600",
                  "is_subscription": "False"
                }
              },
              "data": {
                "type": "orders",
                "id": "8990348",
                "attributes": {
                  "identifier": "cd26194a-693a-44be-8361-be050068d573",
                  "status": "partial_refund",
                  "total": 8656,
                  "refunded": false,
                  "refunded_amount": 3055
                }
              }
            }
            """;

        var webhook = JsonSerializer.Deserialize<Webhook>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        });

        Assert.That(webhook.Meta.EventName, Is.EqualTo("order_refunded"));
        Assert.That(webhook.Data.Attributes.Status, Is.EqualTo("partial_refund"));
        Assert.That(webhook.Data.Attributes.Total, Is.EqualTo(8656));
        Assert.That(webhook.Data.Attributes.RefundedAmount, Is.EqualTo(3055));
        Assert.That(webhook.Meta.CustomData.CoinAmount, Is.EqualTo(21600));
    }
}
