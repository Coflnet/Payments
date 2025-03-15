using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;
public class MigrationService : BackgroundService
{
    private IServiceScopeFactory services;
    private ILogger<MigrationService> logger;
    private IConfiguration Configuration;
    public bool Done { get; private set; }

    public MigrationService(IServiceScopeFactory services, ILogger<MigrationService> logger, IConfiguration configuration)
    {
        this.services = services;
        this.logger = logger;
        Configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var serviceScope = services.CreateScope();
            using var context = serviceScope.ServiceProvider.GetService<PaymentContext>();
            await context.Database.MigrateAsync();

            await AddTransferProduct(context);
            await AddRefundProduct(context);
            await AddGroupForEveryProduct(context);
            Done = true;
        }
        catch (Exception e)
        {
            logger.LogError(e, $"Migrating failed");
            Done = true;
        }
    }

    private static async Task AddGroupForEveryProduct(PaymentContext context)
    {
        var allGrups = await context.Groups.ToListAsync();
        foreach (var product in await context.Products.Where(p => !p.Type.HasFlag(Product.ProductType.DISABLED)).Include(p => p.Groups).ToListAsync())
        {
            if (product.Groups.Where(g => g.Slug == product.Slug).Any())
                continue;
            var group = allGrups.Where(g => g.Slug == product.Slug).FirstOrDefault(new Group()
            {
                Slug = product.Slug,
                Products = new() { product }
            });
            product.Groups.Add(group);
            await context.SaveChangesAsync();
        }
    }


    private static async Task AddTransferProduct(PaymentContext context)
    {
        var tranProduct = new PurchaseableProduct()
        {
            Cost = 1,
            Description = "Transfer of coins to another user",
            Slug = "transfer",
            Type = Product.ProductType.VARIABLE_PRICE | Product.ProductType.TOP_UP,
            Title = "Coin transfer"
        };
        await AddProductIfNotExists(context, tranProduct);
    }

    private static async Task AddRefundProduct(PaymentContext context)
    {
        var tranProduct = new PurchaseableProduct()
        {
            Cost = 1,
            Description = "Revert/refund for another transaction",
            Slug = "revert",
            Type = Product.ProductType.VARIABLE_PRICE,
            Title = "Revert transaction"
        };
        await AddProductIfNotExists(context, tranProduct);
    }

    private static async Task AddProductIfNotExists(PaymentContext context, PurchaseableProduct tranProduct)
    {
        if (!await context.Products.Where(p => p.Slug == tranProduct.Slug).AnyAsync())
        {
            context.Products.Add(tranProduct);
            await context.SaveChangesAsync();
        }
    }
}