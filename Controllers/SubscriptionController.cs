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

    /// <summary>
    /// Resume a cancelled subscription that is still in grace period.
    /// A subscription can only be resumed if it was cancelled but hasn't reached its ends_at date yet.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="subscriptionId">The external subscription ID from LemonSqueezy</param>
    /// <returns>True if successfully resumed</returns>
    [HttpPost]
    [Route("resume/{subscriptionId}")]
    public async Task<bool> ResumeSubscription(string userId, string subscriptionId)
    {
        return await subscriptionService.ResumeSubscription(userId, subscriptionId);
    }
}
