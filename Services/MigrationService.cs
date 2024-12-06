using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services
{
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
                Done = true;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Migrating failed");
                Done = true;
            }
        }


        private async Task MoveInt<T>(DbSet<T> oldDb, PaymentContext context, Func<IQueryable<T>, IQueryable<T>> include = null) where T : class, HasId
        {
            var select = oldDb.OrderBy(d => d.Id);
            if (include != null)
                select = include(select).OrderBy(d => d.Id);
            await MoveData(oldDb, context, select);
        }
        private async Task MoveLongId<T>(DbSet<T> oldDb, PaymentContext context, Func<IQueryable<T>, IQueryable<T>> include = null) where T : class, HasLongId
        {
            var select = oldDb.OrderBy(d => d.Id);
            if (include != null)
                select = include(select).OrderBy(d => d.Id);
            await MoveData(oldDb, context, select);
        }

        private async Task MoveData<T>(DbSet<T> oldDb, PaymentContext context, IOrderedQueryable<T> select) where T : class
        {
            var transactionsBatchSize = 200;
            var transactionCount = await oldDb.CountAsync();
            logger.LogInformation($"Migrating {transactionCount} {oldDb.EntityType.Name}");
            for (int i = 0; i < transactionCount; i += transactionsBatchSize)
            {
                var transactions = await select.Skip(i).Take(transactionsBatchSize).ToListAsync();
                context.AddRange(transactions);
                await context.SaveChangesAsync();
                logger.LogInformation($"Migrated {i + transactions.Count} of {transactionCount} {oldDb.EntityType.Name}");
            }
        }

        public static string GetKeyField(Type type)
        {
            var allProperties = type.GetProperties();

            var keyProperty = allProperties.SingleOrDefault(p => p.IsDefined(typeof(KeyAttribute)));

            return keyProperty != null ? keyProperty.Name : null;
        }

        private static async Task AddTransferProduct(PaymentContext context)
        {
            var tranProduct = new PurchaseableProduct()
            {
                Cost = 1,
                Description = "Transfer of coins to another user",
                Slug = "transfer",
                Type = PurchaseableProduct.ProductType.VARIABLE_PRICE | PurchaseableProduct.ProductType.TOP_UP,
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
                Type = PurchaseableProduct.ProductType.VARIABLE_PRICE,
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
}