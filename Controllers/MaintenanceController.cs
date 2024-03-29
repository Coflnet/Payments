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

    public MaintenanceController(PaymentContext db)
    {
        this.db = db;
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
}

