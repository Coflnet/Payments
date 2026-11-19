using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services;

/// <summary>
/// Resolves the human readable product title shown on purchase confirmations.
/// </summary>
public static class ProductTitleLookup
{
    /// <summary>The title of a top-up or purchasable product, null when unknown (the email falls back to the id).</summary>
    public static async Task<string> GetProductTitleAsync(this PaymentContext db, int productId)
    {
        var topUp = await db.TopUpProducts.Where(p => p.Id == productId)
            .Select(p => p.Title).FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(topUp))
            return topUp;
        return await db.Products.Where(p => p.Id == productId)
            .Select(p => p.Title).FirstOrDefaultAsync();
    }
}
