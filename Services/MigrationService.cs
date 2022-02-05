using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Coflnet.Payments.Services
{
    public class MigrationService : BackgroundService
    {
        private IServiceScopeFactory services;

        public MigrationService(IServiceScopeFactory services)
        {
            this.services = services;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                using (var serviceScope = services.CreateScope())
                {
                    var context = serviceScope.ServiceProvider.GetService<PaymentContext>();
                    await context.Database.MigrateAsync();

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
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Migrating failed \n{e.Message} \n{e.InnerException?.Message}");
            }
        }
    }
}