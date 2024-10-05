using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Coflnet.Payments.Services
{
    public class LicenseService
    {
        private readonly PaymentContext db;
        private readonly ILogger<LicenseService> logger;
        private readonly UserService userService;
        private readonly TransactionService transactionService;
        private readonly ITransactionEventProducer transactionEventProducer;

        public LicenseService(PaymentContext db, ILogger<LicenseService> logger, UserService userService, TransactionService transactionService, ITransactionEventProducer transactionEventProducer)
        {
            this.db = db;
            this.logger = logger;
            this.userService = userService;
            this.transactionService = transactionService;
            this.transactionEventProducer = transactionEventProducer;
        }

        public async Task PurchaseLicense(string userId, string productSlug, string targetId, string reference)
        {
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            var group = await db.Groups.Where(g => g.Slug == productSlug).FirstOrDefaultAsync();

            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = await userService.GetOrCreate(userId);
            var license = await db.Licenses.Where(l => l.Product == product && l.TargetId == targetId && l.UserId == user.Id).FirstOrDefaultAsync();
            if (license == null)
            {
                db.Licenses.Add(new License
                {
                    Product = product,
                    UserId = user.Id,
                    TargetId = targetId,
                    Expires = DateTime.UtcNow.AddSeconds(product.OwnershipSeconds),
                    group = group
                });
            }
            else
            {
                license.Expires = TransactionService.GetNewExpiry(license.Expires, TimeSpan.FromSeconds(product.OwnershipSeconds));
                db.Licenses.Update(license);
            }
            var stackedReference = $"{targetId}.{reference}".Truncate(80);

            var transactionEvent = await transactionService.CreateTransaction(product, user, product.Cost * -1, stackedReference, product.OwnershipSeconds);

            await transactionEventProducer.ProduceEvent(transactionEvent);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
        }

        public async Task<DateTime> HasLicenseUntil(string userId, string productSlug, string targetId)
        {
            return await db.Licenses.Where(l => (l.Product.Slug == productSlug || l.group.Slug == productSlug) && l.TargetId == targetId && l.UserId == db.Users.Where(u => u.ExternalId == userId).First().Id).Select(l => l.Expires).FirstOrDefaultAsync();
        }

        public async Task<License[]> GetUserLicenses(string userId)
        {
            return await db.Licenses.Where(l => l.UserId == db.Users.Where(u => u.ExternalId == userId).First().Id).Include(l => l.group).ToArrayAsync();
        }

        public async Task<License[]> GetUserTargetLicenses(string userId, string targetId)
        {
            return await db.Licenses.Where(l => l.TargetId == targetId && l.UserId == db.Users.Where(u => u.ExternalId == userId).First().Id).Include(l => l.group).ToArrayAsync();
        }

        internal async Task Revert(string userId, int transactionId)
        {
            var transaction = await db.FiniteTransactions.Where(t => t.Id == transactionId).Include(t => t.Product).FirstOrDefaultAsync();
            var target = transaction.Reference.Split('.')[0];
            var license = await db.Licenses.Where(l => l.Product == transaction.Product && l.UserId == db.Users.Where(u => u.ExternalId == userId).First().Id && l.TargetId == target).FirstOrDefaultAsync();
            if (license == null)
            {
                logger.LogWarning($"No license found for user {userId} and product {transaction.Product.Slug} and target {target}");
                return;
            }
            license.Expires = TransactionService.GetNewExpiry(license.Expires, TimeSpan.FromSeconds(-transaction.Product.OwnershipSeconds));
            db.Licenses.Update(license);
            await db.SaveChangesAsync();
            logger.LogInformation($"Reverted license for user {userId} and product {transaction.Product.Slug} and target {target} now expires at {license.Expires}");
        }
    }
}