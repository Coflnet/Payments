using System;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Payments.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
    private readonly PaymentContext db;

    public HealthController(PaymentContext db)
    {
        this.db = db;
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
            return Ok();
        }
        catch (Exception)
        {
            return StatusCode(503);
        }
    }
}
