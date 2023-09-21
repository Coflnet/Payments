using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace Coflnet.Payments.Services
{

    public class TransactionService
    {
        private ILogger<TransactionService> logger;
        private PaymentContext db;
        private UserService userService;
        private ITransactionEventProducer transactionEventProducer;
        private decimal transactionDeflationRate { get; set; }
        private TransferSettings transferSettings { get; set; }
        private GroupService groupService;
        private IRuleEngine ruleEngine;

        public TransactionService(
            ILogger<TransactionService> logger,
            PaymentContext context,
            UserService userService,
            ITransactionEventProducer transactionEventProducer,
            IConfiguration config,
            GroupService groupService,
            IRuleEngine ruleEngine)
        {
            this.logger = logger;
            db = context;
            this.userService = userService;
            this.transactionEventProducer = transactionEventProducer;
            transferSettings = config?.GetSection("TRANSFER").Get<TransferSettings>();
            this.groupService = groupService;
            this.ruleEngine = ruleEngine;
        }

        /// <summary>
        /// Adds a top up to some user
        /// </summary>
        /// <param name="productId">The product purchased</param>
        /// <param name="userId">The user doing the transaction</param>
        /// <param name="reference">External reference data</param>
        /// <param name="customAmount">Custom amount to add as topup, has to be higher than the product cost</param>
        /// <returns></returns>
        public async Task AddTopUp(int productId, string userId, string reference, long customAmount = 0)
        {
            var product = db.TopUpProducts.Where(p => p.Id == productId).FirstOrDefault();

            var changeamount = product.Cost;
            if (customAmount != 0)
                if (customAmount < product.Cost)
                    throw new ApiException("custom amount is to smal for product");
                else
                    changeamount = customAmount;
            await CreateTransactionInTransaction(product, userId, changeamount, reference);
        }


        /// <summary>
        /// Execute a custom topup that changes an users balance in some way
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="topup"></param>
        /// <returns></returns>
        public async Task AddCustomTopUp(string userId, CustomTopUp topup)
        {
            var product = db.TopUpProducts.Where(p => p.Slug == topup.ProductId && p.ProviderSlug == "custom").FirstOrDefault();
            if (product == null)
                throw new ApiException($"{topup.ProductId} is not a valid custom topup option ");
            var changeamount = product.Cost;
            // adjust amount if its valid
            if (topup.Amount != 0 && topup.Amount < product.Cost)
                if (product.Type.HasFlag(Product.ProductType.VARIABLE_PRICE))
                    changeamount = topup.Amount;
                else
                    logger.LogWarning($"Variable price is disabled for {topup.ProductId} but a value of {topup.Amount} was passed");
            await CreateTransactionInTransaction(product, userId, changeamount, topup.Reference);
        }

        public async Task CreateTransactionInTransaction(TopUpProduct product, string userId, decimal changeamount, string reference)
        {
            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
            if (user == null)
            {
                await db.Database.RollbackTransactionAsync();
                throw new ApiException("user doesn't exist");
            }
            await CreateAndProduceTransaction(product, user, changeamount, reference);
        }
        public async Task CreateTransactionInTransaction(TopUpProduct product, User user, decimal changeamount, string reference)
        {
            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            user = await db.Users.Where(u => u.Id == user.Id).FirstOrDefaultAsync(); // reload user for transaction lock
            await CreateAndProduceTransaction(product, user, changeamount, reference);
        }

        private async Task CreateAndProduceTransaction(TopUpProduct product, User user, decimal changeamount, string reference)
        {
            var transactionEvent = await CreateTransaction(product, user, changeamount, reference);

            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        private async Task<TransactionEvent> CreateTransaction(Product product, User user, decimal changeamount, string reference = "", long adjustedOwnerShipTime = 0)
        {
            var transaction = new FiniteTransaction()
            {
                Product = product,
                Amount = changeamount,
                Reference = reference,
                User = user
            };
            var exists = await db.FiniteTransactions.Where(f =>
                f.Product == product
                && f.User == user
                && f.Reference == reference).AnyAsync();
            if (exists)
                throw new DupplicateTransactionException();
            db.FiniteTransactions.Add(transaction);
            user.Balance += changeamount;
            if (user.Balance < 0 && product.Slug != "revert" && changeamount < 0)
                throw new InsufficientFundsException(changeamount, user.Balance);
            db.Update(user);
            await db.SaveChangesAsync();
            var transactionEvent = new TransactionEvent()
            {
                Amount = Decimal.ToDouble(changeamount),
                Id = transaction.Id,
                OwnedSeconds = adjustedOwnerShipTime == 0 ? product.OwnershipSeconds : adjustedOwnerShipTime,
                ProductId = product.Id,
                ProductSlug = product.Slug,
                Reference = reference,
                UserId = user.ExternalId,
                Timestamp = transaction.Timestamp,
                ProductType = product.Type
            };
            return transactionEvent;
        }

        /// <summary>
        /// Purchase a product 
        /// </summary>
        /// <param name="productSlug"></param>
        /// <param name="userId"></param>
        /// <param name="price"></param>
        /// <returns></returns>
        public async Task PurchaseProduct(string productSlug, string userId, decimal price = 0)
        {
            PurchaseableProduct product = await GetProduct(productSlug);
            if (!product.Type.HasFlag(PurchaseableProduct.ProductType.VARIABLE_PRICE))
                price = product.Cost;
            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = await userService.GetOrCreate(userId);
            if (user.Owns.Where(p => p.Product == product && p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
                throw new ApiException("already owned");
            if (user.AvailableBalance < price || price < 0)
                throw new ApiException("insuficcient balance");

            var transactionEvent = await CreateTransaction(product, user, price * -1);
            user.Owns.Add(new OwnerShip() { Expires = DateTime.UtcNow.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        private async Task<PurchaseableProduct> GetProduct(string productSlug)
        {
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            if (product == null)
                throw new ApiException($"product {productSlug} could not be found ");
            if (product.Type.HasFlag(PurchaseableProduct.ProductType.DISABLED))
                throw new ApiException("product can't be purchased");
            return product;
        }

        public async Task PurchaseServie(string productSlug, string userId, int count, string reference)
        {
            var dbProduct = await GetProduct(productSlug);
            if (!dbProduct.Type.HasFlag(PurchaseableProduct.ProductType.SERVICE))
                throw new ApiException("product is not a service");

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = await userService.GetOrCreate(userId);
            var adjustedProduct = (await ruleEngine.GetAdjusted(dbProduct, user)).ModifiedProduct;
            await ExecuteServicePurchase(productSlug, userId, count, reference, dbProduct, transaction, user, adjustedProduct);
        }

        private async Task<TransactionEvent> ExecuteServicePurchase(string productSlug, string userId, int count, string reference, Product dbProduct, IDbContextTransaction transaction, User user, Product adjustedProduct)
        {
            var existingOwnerShip = user.Owns?.Where(p => p.Product == dbProduct) ?? new List<OwnerShip>();
            if (existingOwnerShip.Where(p => p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
            {
                await transaction.RollbackAsync();
                throw new ApiException("already owned for too long");
            }
            var price = adjustedProduct.Cost * count;
            if (user.AvailableBalance < price && adjustedProduct.Slug != "revert")
            {
                await transaction.RollbackAsync();
                logger.LogError($"User {user.ExternalId} doesn't have the required {price} amount to purchase {productSlug} (only {user.AvailableBalance} available)");
                throw new ApiException("insuficcient balance");
            }

            var allProductsToExtend = await db.Products.Where(p => p.Slug == productSlug).SelectMany(p => p.Groups, (p, g) => g.Products.Where(p => p.Slug == g.Slug).First()).ToListAsync();

            var transactionEvent = await CreateTransaction(dbProduct, user, price * -1, reference, adjustedProduct.OwnershipSeconds);
            var time = TimeSpan.FromSeconds(adjustedProduct.OwnershipSeconds * count);
            foreach (var item in allProductsToExtend)
            {
                var existingExpiry = await userService.GetLongest(userId, new() { item.Slug });
                Console.WriteLine(item.Slug + " exires at " + existingExpiry);
                var newExpiry = GetNewExpiry(existingExpiry, time);
                logger.LogInformation($"User {user.ExternalId} has {existingExpiry} for {item.Slug} and will be extended to {newExpiry} by {time}");
                existingOwnerShip = user.Owns?.Where(p => p.Product.Id == item.Id);
                if (existingOwnerShip.Any())
                {
                    existingOwnerShip.First().Expires = newExpiry;
                }
                else
                {
                    user.Owns.Add(new OwnerShip() { Expires = newExpiry, Product = item as PurchaseableProduct, User = user });
                }
            }

            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
            return transactionEvent;
        }

        internal async Task<TransactionEvent> RevertPurchase(string userId, long transactionId)
        {
            var transaction = db.FiniteTransactions.Where(t => t.User == db.Users.Where(u => u.ExternalId == userId).First() && t.Id == transactionId).Include(t => t.Product).FirstOrDefault();
            var dbProduct = await GetProduct("revert");

            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = await userService.GetOrCreate(userId);
            var adjustedProduct = (await ruleEngine.GetAdjusted(dbProduct, user)).ModifiedProduct;
            var count = (int)Math.Round(transaction.Amount / transaction.Product.Cost);
            adjustedProduct.Cost = -transaction.Amount / count;
            adjustedProduct.OwnershipSeconds = -transaction.Product.OwnershipSeconds;
            adjustedProduct.Slug = "revert";
            return await ExecuteServicePurchase(transaction.Product.Slug, userId, -count, $"revert transaction " + transactionId, dbProduct, dbTransaction, user, adjustedProduct);
        }

        private static DateTime GetNewExpiry(DateTime currentTime, TimeSpan time)
        {
            if (currentTime < DateTime.UtcNow)
                return DateTime.UtcNow + time;
            else
                return currentTime += time;
        }

        internal async Task<TransactionEvent> Transfer(string userId, string targetUserId, decimal changeamount, string reference)
        {
            if (changeamount < 1)
                throw new ApiException("The minimum transaction amount is 1");

            using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var product = db.Products.Where(p => p.Slug == "transfer").FirstOrDefault();
            var initiatingUser = await userService.GetAndInclude(userId, u => u);
            var minTime = DateTime.UtcNow - TimeSpan.FromDays(transferSettings.PeriodDays);
            var transactionCount = await db.FiniteTransactions.Where(t => t.User == initiatingUser && t.Product == product && t.Timestamp > minTime).CountAsync();
            if (transactionCount >= transferSettings.Limit)
                throw new ApiException($"You reached the maximium of {transferSettings.Limit} transactions per {transferSettings.PeriodDays} days");
            var targetUser = await userService.GetOrCreate(targetUserId);
            await AssertNotToManyTransfersReceived(product, minTime, targetUser);
            var senderDeduct = -(changeamount);
            if (db.FiniteTransactions.Where(t =>
                 t.Amount == senderDeduct && t.Product == product && t.Reference == reference && t.User == initiatingUser)
                .Any())
                throw new DupplicateTransactionException();

            var transactionEvent = await CreateTransaction(product, initiatingUser, senderDeduct, reference);
            var receiveTransaction = await CreateTransaction(product, targetUser, changeamount, reference);

            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
            await transactionEventProducer.ProduceEvent(receiveTransaction);
            return transactionEvent;
        }

        private async Task AssertNotToManyTransfersReceived(PurchaseableProduct product, DateTime minTime, User targetUser)
        {
            var received = await db.FiniteTransactions.Where(t => t.User == targetUser && t.Product == product && t.Timestamp > minTime && t.Amount > 0).ToListAsync();
            if (received.Count(r => r.Amount > 100) <= transferSettings.Limit / 2 && (received.Sum(r => r.Amount) <= (transferSettings.Limit * 100) || received.Count <= transferSettings.Limit / 2))
                return;
            var ends = received.Select(r => r.Timestamp).OrderBy(d => d).First() + TimeSpan.FromDays(transferSettings.PeriodDays) - DateTime.UtcNow;
            throw new ApiException($"The target user has received too many transfers recently. Can receive again in {(int)ends.TotalHours + 1} hours");
        }

        /// <summary>
        /// Thrown if the transaction was already executed
        /// </summary>
        public class DupplicateTransactionException : ApiException
        {
            /// <summary>
            /// Creates a new instance <see cref="DupplicateTransactionException"/>
            /// </summary>
            /// <returns></returns>
            public DupplicateTransactionException() : base("This transaction already happened (same reference found)")
            {
            }
        }
        /// <summary>
        /// Thrown if an user doesn't have enough funds
        /// </summary>
        public class InsufficientFundsException : ApiException
        {
            /// <summary>
            /// Creates a new instance <see cref="InsufficientFundsException"/>
            /// </summary>
            /// <returns></returns>
            public InsufficientFundsException(decimal required, decimal available) : base($"You don't have enough funds to make this transaction. Required {required} Available: {available}")
            {
            }
        }
    }
}