using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Coflnet.Payments.Models.GooglePay;
using Coflnet.Payments.Services;
using Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers;

/// <summary>Google Play subscription eligibility and purchase completion.</summary>
[ApiController]
[Route("api/googleplay/subscription")]
public class GooglePlaySubscriptionController(SubscriptionService subscriptions, ILogger<GooglePlaySubscriptionController> logger = null) : ControllerBase
{
    /// <summary>Checks whether this account can start the plan in Google Play.</summary>
    [HttpGet("eligibility")]
    public Task<GooglePlayEligibility> Eligibility([FromQuery, Required] string userId, [FromQuery, Required] string productSlug) =>
        subscriptions.GooglePlayEligibility(userId, productSlug);

    /// <summary>Verifies a purchase with Google Play and grants any paid period.</summary>
    /// <response code="409">Another request is processing this subscription. The app can retry when status is "processing".</response>
    [HttpPost("complete")]
    public async Task<ActionResult<GooglePlaySubscriptionResult>> Complete([FromBody] GooglePlaySubscriptionRequest request)
    {
        if (request.PackageName != SubscriptionService.GooglePlayPackage)
            return new GooglePlaySubscriptionResult(false, null, "invalid_package");
        try
        {
            return await subscriptions.SyncGooglePlay(request.PurchaseToken, request.UserId);
        }
        catch (SubscriptionBusyException)
        {
            return Conflict(new GooglePlaySubscriptionResult(false, "processing", null));
        }
        catch (Exception e)
        {
            logger?.LogError("Google Play subscription completion failed ({ExceptionType}, HTTP {StatusCode})",
                e.GetType().Name, (int?)(e as GoogleApiException)?.HttpStatusCode);
            return StatusCode(500, new GooglePlaySubscriptionResult(false, null, "verification_unavailable"));
        }
    }
}
