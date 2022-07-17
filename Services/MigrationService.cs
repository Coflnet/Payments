using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services
{
    public class MigrationService : BackgroundService
    {
        private IServiceScopeFactory services;
        private ILogger<MigrationService> logger;

        public MigrationService(IServiceScopeFactory services, ILogger<MigrationService> logger)
        {
            this.services = services;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var serviceScope = services.CreateScope();
                using var context = serviceScope.ServiceProvider.GetService<PaymentContext>();
                await context.Database.MigrateAsync();
                logger.LogInformation("Model Migration completed");

                if (!await context.Products.Where(p => p.Slug == "transfer").AnyAsync())
                {
                    var tranProduct = new PurchaseableProduct()
                    {
                        Cost = 1,
                        Description = "Transfer of coins to another user",
                        Slug = "transfer",
                        Type = PurchaseableProduct.ProductType.VARIABLE_PRICE | PurchaseableProduct.ProductType.TOP_UP,
                        Title = "Coin transfer"
                    };
                    context.Products.Add(tranProduct);
                    await context.SaveChangesAsync();
                }
                var allGrups = await context.Groups.ToListAsync();
                // add group for every product
                foreach (var product in await context.Products.Where(p => !p.Type.HasFlag(Product.ProductType.DISABLED)).Include(p => p.Groups).ToListAsync())
                {
                    if (product.Groups.Where(g => g.Slug == product.Slug).Any())
                        continue;
                    var group = allGrups.Where(g => g.Slug == product.Slug).FirstOrDefault(new Group()
                    {
                        Slug = product.Slug,
                        Products = new() { product }
                    });
                    
                    //context.Groups.Add(group);
                    product.Groups.Add(group);
                    await context.SaveChangesAsync();
                }
                logger.LogInformation("Data Migration completed");
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Migrating failed");
            }
        }
    }
}