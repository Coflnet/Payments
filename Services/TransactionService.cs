using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

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

        public TransactionService(
            ILogger<TransactionService> logger,
            PaymentContext context,
            UserService userService,
            ITransactionEventProducer transactionEventProducer,
            IConfiguration config)
        {
            this.logger = logger;
            db = context;
            this.userService = userService;
            this.transactionEventProducer = transactionEventProducer;
            transferSettings = config?.GetSection("TRANSFER").Get<TransferSettings>();

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
            var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
            if (user == null)
                throw new ApiException("user doesn't exist");
            await db.Database.BeginTransactionAsync();
            var changeamount = product.Cost;
            if(customAmount != 0)
                if(customAmount < product.Cost)
                throw new ApiException("custom amount is to smal for product");
                else
                    changeamount = customAmount;
            await CreateAndProduceTransaction(product, user, changeamount, reference);
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
            var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
            if (user == null)
                throw new ApiException("user doesn't exist");
            var changeamount = product.Cost;
            // adjust amount if its valid
            if (topup.Amount != 0 && topup.Amount < product.Cost)
                if (product.Type.HasFlag(Product.ProductType.VARIABLE_PRICE))
                    changeamount = topup.Amount;
                else
                    logger.LogWarning($"Variable price is disabled for {topup.ProductId} but a value of {topup.Amount} was passed");
            await CreateTransactionInTransaction(product, user, changeamount, topup.Reference);
        }

        public async Task CreateTransactionInTransaction(TopUpProduct product, User user, decimal changeamount, string reference)
        {
            await db.Database.BeginTransactionAsync();
            await CreateAndProduceTransaction(product, user, changeamount, reference);
        }

        private async Task CreateAndProduceTransaction(TopUpProduct product, User user, decimal changeamount, string reference)
        {
            var transactionEvent = await CreateTransaction(product, user, changeamount, reference);

            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        private async Task<TransactionEvent> CreateTransaction(Product product, User user, decimal changeamount, string reference = "")
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
            if (user.Balance < 0)
                throw new InsufficientFundsException(changeamount, user.Balance);
            db.Update(user);
            await db.SaveChangesAsync();
            var transactionEvent = new TransactionEvent()
            {
                Amount = Decimal.ToDouble(changeamount),
                Id = transaction.Id,
                OwnedSeconds = product.OwnershipSeconds,
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
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            if(product == null)
                throw new ApiException($"product {productSlug} could not be found ");
            if (!product.Type.HasFlag(PurchaseableProduct.ProductType.VARIABLE_PRICE))
                price = product.Cost;
            if (product.Type == PurchaseableProduct.ProductType.DISABLED)
                throw new ApiException("product can't be purchased");
            var user = await userService.GetOrCreate(userId);
            if (user.Owns.Where(p => p.Product == product && p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
                throw new ApiException("already owned");
            if (user.AvailableBalance < price)
                throw new ApiException("insuficcient balance");

            await db.Database.BeginTransactionAsync();
            var transactionEvent = await CreateTransaction(product, user, price * -1);
            user.Owns.Add(new OwnerShip() { Expires = DateTime.UtcNow.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        public async Task PurchaseServie(string productSlug, string userId, int count, string reference)
        {
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            if (product.Type.HasFlag(PurchaseableProduct.ProductType.DISABLED))
                throw new ApiException("product can't be purchased");
            if (!product.Type.HasFlag(PurchaseableProduct.ProductType.SERVICE))
                throw new ApiException("product is not a service");
            var user = await userService.GetOrCreate(userId);
            var existingOwnerShip = user.Owns?.Where(p => p.Product == product) ?? new List<OwnerShip>();
            if (existingOwnerShip.Where(p => p.Expires > DateTime.UtcNow + TimeSpan.FromDays(3000)).Any())
                throw new ApiException("already owned for too long");
            var price = product.Cost * count;
            if (user.AvailableBalance < price)
                throw new ApiException("insuficcient balance");

            await db.Database.BeginTransactionAsync();
            var transactionEvent = await CreateTransaction(product, user, price * -1, reference);
            var time = TimeSpan.FromSeconds(product.OwnershipSeconds * count);
            if (existingOwnerShip.Any())
            {
                var currentTime = existingOwnerShip.First().Expires;
                if(currentTime < DateTime.UtcNow)
                    existingOwnerShip.First().Expires = DateTime.Now + time;
                else
                    existingOwnerShip.First().Expires += time;
            }
            else
                user.Owns.Add(new OwnerShip() { Expires = DateTime.UtcNow + time, Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        internal async Task<TransactionEvent> Transfer(string userId, string targetUserId, decimal changeamount, string reference)
        {
            if (changeamount < 1)
                throw new ApiException("The minimum transaction amount is 1");
            var product = db.Products.Where(p => p.Slug == "transfer").FirstOrDefault();
            var initiatingUser = await userService.GetAndInclude(userId, u=>u);
            var minTime = DateTime.UtcNow - TimeSpan.FromDays(30);
            var transactionCount = db.FiniteTransactions.Where(t=>t.User == initiatingUser && t.Product == product && t.Timestamp > minTime).Count();
            if(transactionCount >= transferSettings.Limit)
                throw new ApiException($"You reached the maximium of {transferSettings.Limit} transactions per {transferSettings.PeriodDays} days");
            var targetUser = await userService.GetOrCreate(targetUserId);
            await db.Database.BeginTransactionAsync();
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

        /// <summary>
        /// Thrown if the transaction was already executed
        /// </summary>
        public class DupplicateTransactionException : ApiException
        {
            /// <summary>
            /// Creates a new instance <see cref="DupplicateTransactionException"/>
            /// </summary>
            /// <returns></returns>
            public DupplicateTransactionException() : base("This transaction already happened (same reference)")
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