using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services
{

    public class TransactionService
    {
        private ILogger<TransactionService> logger;
        private PaymentContext db;
        private UserService userService;
        private ITransactionEventProducer transactionEventProducer;

        public TransactionService(
            ILogger<TransactionService> logger,
            PaymentContext context,
            UserService userService, 
            ITransactionEventProducer transactionEventProducer)
        {
            this.logger = logger;
            db = context;
            this.userService = userService;
            this.transactionEventProducer = transactionEventProducer;
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
            if(user == null)
                throw new Exception("user doesn't exist");
            await db.Database.BeginTransactionAsync();
            var changeamount = product.Cost;
            await CreateTransaction(product, user, changeamount, reference);

            await db.Database.CommitTransactionAsync();
        }

        private async Task CreateTransaction(PurchaseableProduct product, User user, decimal changeamount, string reference = "")
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
            db.Update(user);
            await db.SaveChangesAsync();
            await transactionEventProducer.ProduceEvent(new TransactionEvent()
            {
                Amount = Decimal.ToDouble(changeamount),
                Id = transaction.Id,
                OwnedSeconds = product.OwnershipSeconds,
                ProductId = product.Id,
                ProductSlug = product.Slug,
                Reference = reference,
                UserId = user.ExternalId,
                Timestamp = transaction.Timestamp
            });
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
            await CreateTransaction(product, user, price * -1);
            user.Owns.Add(new OwnerShip() { Expires = DateTime.Now.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
        }
    }
}