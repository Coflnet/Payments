using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace Coflnet.Payments.Services;

public class LemonSqueezyService
{
    private IConfiguration config;
    private ILogger<LemonSqueezyService> logger;

    public LemonSqueezyService(IConfiguration config, ILogger<LemonSqueezyService> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    public async Task CancelSubscription(string subscriptionId)
    {
        var restclient = new RestClient($"https://api.lemonsqueezy.com/v1/subscriptions/{subscriptionId}");
        var request = CreateRequest(Method.Delete);
        var response = await restclient.ExecuteAsync(request);
        logger.LogInformation(response.Content);
    }

    public async Task<TopUpIdResponse> NewMethod(TopUpOptions options, User user, Product product, decimal eurPrice, decimal coinAmount, string variantId, bool isSubscription)
    {
        var restclient = new RestClient("https://api.lemonsqueezy.com/v1/checkouts");
        RestRequest request = CreateRequest(Method.Post);
        var createData = new
        {
            data = new
            {
                type = "checkouts",
                attributes = new
                {
                    custom_price = (int)(eurPrice * 100),
                    product_options = new
                    {
                        name = product.Title,
                        redirect_url = options?.SuccessUrl ?? config["DEFAULT:SUCCESS_URL"],
                        receipt_button_text = "Go to your account",
                        description = product.Description ?? "Will be credited to your account",
                    },
                    checkout_options = new
                    {
                        subscription_preview = true
                    },
                    checkout_data = new
                    {
                        email = options?.UserEmail,
                        custom = new
                        {
                            user_id = user.ExternalId.ToString(),
                            product_id = product.Id.ToString(),
                            coin_amount = ((int)coinAmount).ToString(),
                            is_subscription = isSubscription.ToString()
                        },
                    },
                    expires_at = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                relationships = new
                {
                    store = new
                    {
                        data = new
                        {
                            type = "stores",
                            id = config["LEMONSQUEEZY:STORE_ID"]
                        }
                    },
                    variant = new
                    {
                        data = new
                        {
                            type = "variants",
                            id = variantId
                        }
                    }
                }
            }
        };
        var json = JsonConvert.SerializeObject(createData, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
        request.AddJsonBody(json);
        logger.LogInformation($"Creating lemonsqueezy checkout with: \n{json}");
        var response = await restclient.ExecuteAsync(request);
        logger.LogInformation(response.Content);
        var result = JsonConvert.DeserializeObject(response.Content);
        var data = JObject.Parse(result.ToString());
        var checkoutId = (string)data["data"]["id"];
        var link = (string)data["data"]["attributes"]["url"];
        return new TopUpIdResponse()
        {
            DirctLink = link,
            Id = checkoutId
        };
    }

    private RestRequest CreateRequest(Method method)
    {
        var request = new RestRequest("", method);
        request.AddHeader("Accept", "application/vnd.api+json");
        request.AddHeader("Content-Type", "application/vnd.api+json");
        request.AddHeader("Authorization", "Bearer " + config["LEMONSQUEEZY:API_KEY"]);
        return request;
    }
}
