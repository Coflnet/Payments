using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Payments.Controllers;
[ApiController]
[Route("[controller]")]
public class MaintenanceController : ControllerBase
{
    private readonly PaymentContext db;
    private readonly MigrationService migrationService;

    public MaintenanceController(PaymentContext db, MigrationService migrationService)
    {
        this.db = db;
        this.migrationService = migrationService;
    }

    /// <summary>
    /// Extends the lifetime of all services by the given amount
    /// </summary>
    /// <param name="seconds"></param>
    /// <param name="startTime"></param>
    /// <param name="checkSum"></param>
    /// <returns></returns>
    [HttpPost]
    [Route("extend")]
    public async Task<int> ExtendLifetime(int seconds, DateTime startTime = default, long checkSum = 0)
    {
        if (startTime == default)
            startTime = DateTime.UtcNow;
        var ownerships = await db.OwnerShips.Where(o => o.Expires > startTime && o.Product.Type.HasFlag(Product.ProductType.SERVICE)).ToListAsync();
        foreach (var ownership in ownerships)
        {
            ownership.Expires = ownership.Expires.AddSeconds(seconds);
        }
        var sum = ownerships.Sum(o => o.Expires.Ticks % 123456789);
        if (sum != checkSum)
            throw new ApiException($"Checksums don't match {sum} != {checkSum}");
        await db.SaveChangesAsync();
        return ownerships.Count;
    }

    [HttpGet]
    [Route("/migrationdone")]
    public IActionResult MigrationDone()
    {
        if (migrationService.Done)
            return Ok(migrationService.Done);
        return StatusCode(503);
    }

    [HttpGet]
    [Route("/ownership")]
    public async Task<List<OwnerShipMap>> GetOwnerships(int start, int count)
    {
        var all = await db.OwnerShips.Include(o => o.Product).OrderBy(o => o.Id).Where(o => o.Id > start).Take(count).Select(o => new { o, o.User.ExternalId }).ToListAsync();
        return all.Select(o => new OwnerShipMap()
        {
            UserId = o.ExternalId,
            ProductSlug = o.o.Product.Slug,
            Expires = o.o.Expires
        }).ToList();
    }

    public class OwnerShipMap
    {
        public string UserId { get; set; }
        public string ProductSlug { get; set; }
        public DateTime Expires { get; set; }
    }

}

