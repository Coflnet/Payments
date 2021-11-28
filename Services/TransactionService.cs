using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Payments.Services
{

    public class TransactionService
    {
        private ILogger<TransactionService> logger;
        private PaymentContext db;
        private UserService userService;
        private ITransactionEventProducer transactionEventProducer;
        private decimal transactionDeflationRate { get; set; }

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
            transactionDeflationRate = config.GetValue<decimal>("TRANSACTION_DEFLATE");
            logger.LogInformation("deflation rate is: " + transactionDeflationRate);

        }

        /// <summary>
        /// Adds a top up to some user
        /// </summary>
        /// <param name="productId">The product purchased</param>
        /// <param name="userId">The user doing the transaction</param>
        /// <param name="reference">External reference data</param>
        /// <returns></returns>
        public async Task AddTopUp(int productId, string userId, string reference)
        {
            var product = db.Products.Where(p => p.Id == productId).FirstOrDefault();
            var user = db.Users.Where(u => u.ExternalId == userId).FirstOrDefault();
            if (user == null)
                throw new Exception("user doesn't exist");
            await db.Database.BeginTransactionAsync();
            var changeamount = product.Cost;
            var transactionEvent = await CreateTransaction(product, user, changeamount, reference);

            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        private async Task<TransactionEvent> CreateTransaction(PurchaseableProduct product, User user, decimal changeamount, string reference = "")
        {
            var transaction = new FiniteTransaction()
            {
                Product = product,
                Amount = changeamount,
                Reference = reference,
                User = user
            };
            db.FiniteTransactions.Add(transaction);
            user.Balance += changeamount;
            if (user.Balance < 0)
                throw new InsufficientFundsException();
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
                Timestamp = transaction.Timestamp
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
            if (!product.Type.HasFlag(PurchaseableProduct.ProductType.VARIABLE_PRICE))
                price = product.Cost;
            if (product.Type == PurchaseableProduct.ProductType.DISABLED)
                throw new Exception("product can't be purchased");
            var user = await userService.GetOrCreate(userId);
            if (user.Owns.Where(p => p.Product == product && p.Expires > DateTime.Now + TimeSpan.FromDays(3000)).Any())
                throw new Exception("already owned");
            if (user.AvailableBalance < price)
                throw new Exception("insuficcient balance");

            user.Balance -= price;
            await db.Database.BeginTransactionAsync();
            var transactionEvent = await CreateTransaction(product, user, price * -1);
            user.Owns.Add(new OwnerShip() { Expires = DateTime.Now.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
            await transactionEventProducer.ProduceEvent(transactionEvent);
        }

        internal async Task<TransactionEvent> Transfer(string userId, string targetUserId, decimal changeamount, string reference)
        {
            if (changeamount < 1)
                throw new Exception("The minimum transaction amount is 1");
            var product = db.Products.Where(p => p.Slug == "transfer").FirstOrDefault();
            var initiatingUser = await userService.GetOrCreate(userId);
            var targetUser = await userService.GetOrCreate(targetUserId);
            await db.Database.BeginTransactionAsync();
            var senderDeduct = -(changeamount + changeamount * transactionDeflationRate);
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
        public class DupplicateTransactionException : Exception
        {
            /// <summary>
            /// Creates a new instance <see cref="DupplicateTransactionException"/>
            /// </summary>
            /// <returns></returns>
            public DupplicateTransactionException() : base("This transaction already happened")
            {
            }
        }
        /// <summary>
        /// Thrown if an user doesn't have enough funds
        /// </summary>
        public class InsufficientFundsException : Exception
        {
            /// <summary>
            /// Creates a new instance <see cref="InsufficientFundsException"/>
            /// </summary>
            /// <returns></returns>
            public InsufficientFundsException() : base("You don't have enough funds to make this transaction")
            {
            }
        }
    }
}