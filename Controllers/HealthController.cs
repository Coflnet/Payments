using System;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
    private static int lastCheckFailed;
    private readonly PaymentContext db;
    private readonly ILogger<HealthController> logger;

    public HealthController(PaymentContext db, ILogger<HealthController> logger)
    {
        this.db = db;
        this.logger = logger;
    }

    /// <summary>
    /// Readiness probe: 200 when the database answers <c>SELECT 1</c> in time, 503 otherwise.
    /// </summary>
    [HttpGet]
    [Route("/health/ready")]
    public async Task<IActionResult> Ready()
    {
        using var timeout = new CancellationTokenSource(ReadyTimeout);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            Interlocked.Exchange(ref lastCheckFailed, 0);
            return Ok();
        }
        catch (Exception e)
        {
            LogFailureOnStateChange(e);
            return StatusCode(503);
        }
    }

    // Log only the exception type (no message, it may carry connection details)
    // and only when the probe flips from healthy to failing.
    private void LogFailureOnStateChange(Exception e)
    {
        if (Interlocked.Exchange(ref lastCheckFailed, 1) == 1)
            return;
        logger.LogWarning("Readiness database check failed with {ExceptionType}", e.GetType().Name);
    }
}
