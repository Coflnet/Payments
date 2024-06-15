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
                using var serviceScope = services.CreateScope();
                using var context = serviceScope.ServiceProvider.GetService<PaymentContext>();
                var failed = false;
                using (var oldDb = serviceScope.ServiceProvider.GetService<OldPaymentContext>())
                    failed = await MigrateData(oldDb, context);
                if (failed)
                {
                    logger.LogError("Data Migration failed");
                    return;
                }
                if (Configuration["DB_CONNECTION"].StartsWith("server"))
                {
                    logger.LogWarning("Using deprecated MariaDB, not applying migrations");
                }
                else
                    await context.Database.MigrateAsync();
                logger.LogInformation("Model Migration completed");
                await AddTransferProduct(context);
                await AddRefundProduct(context);
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
                var userCount = await context.Users.CountAsync();
                logger.LogInformation("Data Migration completed, {userCount} users active", userCount);
                Done = true;
            }
            catch (Exception e)
            {
                logger.LogError(e, $"Migrating failed");
                Done = true;
            }
        }

        private async Task<bool> MigrateData(OldPaymentContext oldDb, PaymentContext context)
        {
            /*if (oldDb == null)
            {
                logger.LogWarning("No old database connection configured");
                return false;
            }
            var i = 0;
            var toUpdate = context.OwnerShips.Where(o => o.UserId == null).Select(o => o.Id).OrderByDescending(id => id);
            var ids = new HashSet<long>(toUpdate);
            var batchSize = 50;
            logger.LogInformation($"Migrating {ids.Count} OwnerShips currently null");
            for (int j = 0; j < ids.Count; j += batchSize)
            {
                var batch = ids.Skip(j).Take(batchSize);
                var toBeupdated = context.OwnerShips.Where(o => batch.Contains(o.Id));
                var idLookup = await oldDb.OwnerShips.Where(o => batch.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
                foreach (var item in toBeupdated)
                {
                    item.UserId = idLookup[item.Id].UserId;
                }
                await context.SaveChangesAsync();
                logger.LogInformation($"Migrated {j + batchSize} OwnerShips");
            }

            for (int ib = 0; ib < 25; ib++)
            {

                var transactionsToupdate = context.FiniteTransactions.Where(o => o.ProductId == null && o.Id < ib * 10000).Select(o => o.Id).OrderByDescending(id => id);
                ids = new HashSet<long>(transactionsToupdate);
                logger.LogInformation($"Migrating {ids.Count} FiniteTransactions currently null");
                for (int j = 0; j < ids.Count; j += batchSize)
                {
                    var batch = ids.Skip(j).Take(batchSize);
                    var toBeupdated = context.FiniteTransactions.Where(o => batch.Contains(o.Id));
                    var idLookup = await oldDb.FiniteTransactions.Where(o => batch.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
                    foreach (var item in toBeupdated)
                    {
                        item.ProductId = idLookup[item.Id].ProductId;
                    }
                    await context.SaveChangesAsync();
                    logger.LogInformation($"Migrated {j + batchSize} FiniteTransactions");
                }
            }

            var paymentRequestsToupdate = context.PaymentRequests.Where(o => o.UserId == null).Select(o => o.Id).OrderByDescending(id => id);
            var intIds = new HashSet<int>(paymentRequestsToupdate);
            logger.LogInformation($"Migrating {intIds.Count} PaymentRequests currently null");
            for (int j = 0; j < intIds.Count; j += batchSize)
            {
                var batch = intIds.Skip(j).Take(batchSize);
                var toBeupdated = context.PaymentRequests.Where(o => batch.Contains(o.Id));
                var idLookup = await oldDb.PaymentRequests.Where(o => batch.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
                foreach (var item in toBeupdated)
                {
                    item.UserId = idLookup[item.Id].UserId;
                }
                await context.SaveChangesAsync();
                logger.LogInformation($"Migrated {j + batchSize} PaymentRequests");
            }
            //await MoveInt(oldDb.Users, context);
            //await MoveLongId(oldDb.PlanedTransactions, context); done
            // await MoveInt(oldDb.Products, context);
            // await MoveInt(oldDb.TopUpProducts, context);
            // await MoveInt(oldDb.Groups, context);
            // await MoveInt(oldDb.Rules, context);*/
            return false;
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