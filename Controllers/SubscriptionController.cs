using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers;
[ApiController]
[Route("/api/[controller]")]
public class SubscriptionController : ControllerBase
{
    private ILogger<SubscriptionController> logger;
    private SubscriptionService subscriptionService;

    public SubscriptionController(ILogger<SubscriptionController> logger, SubscriptionService subscriptionService)
    {
        this.logger = logger;
        this.subscriptionService = subscriptionService;
    }

    [HttpGet]
    [Route("u/{userId}")]
    public async Task<IEnumerable<UserSubscription>> GetUserSubscriptions(string userId)
    {
        return await subscriptionService.GetUserSubscriptions(userId);
    }

    [HttpDelete]
    [Route("cancel/{subscriptionId}")]
    public async Task CancelSubscription(string userId, string subscriptionId)
    {
        await subscriptionService.CancelSubscription(userId, subscriptionId);
    }
}
