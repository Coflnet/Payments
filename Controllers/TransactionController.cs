using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// Handles users
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class TransactionController : ControllerBase
    {
        private readonly ILogger<UserController> _logger;

        private PaymentContext db { get; }

        private UserService userService;

        public TransactionController(
            ILogger<UserController> logger,
            PaymentContext context,
            UserService userService
        )
        {
            _logger = logger;
            db = context;
            this.userService = userService;
        }

        [HttpGet]
        [Route("u/{userId}")]
        public async Task<List<ExternalTransaction>> Purchase(string userId)
        {
            var user = await userService.GetOrCreate(userId);
            return await db.FiniteTransactions.Where(f => f.User == user).Select(selector).ToListAsync();
        }

        [HttpGet]
        [Route("planed/u/{userId}")]
        public async Task<List<ExternalTransaction>> PlanedTransactions(string userId)
        {
            var user = await userService.GetOrCreate(userId);
            return await db.PlanedTransactions.Where(f => f.User == user).Select(selector).ToListAsync();
        }

        System.Linq.Expressions.Expression<Func<Transaction, ExternalTransaction>> selector = t => new ExternalTransaction()
        {
            Id = t.Id.ToString(),
            Amount = t.Amount,
            ProductId = t.Product.Slug,
            Reference = t.Reference
        };

        [HttpPost]
        [Route("planed/u/{userId}")]
        public async Task<PlanedTransaction> PlanedTransactions(string userId, [FromBody] ExternalTransaction transaction)
        {
            var user = await userService.GetOrCreate(userId);
            //var product = await 
            var trans = new PlanedTransaction()
            {
                User = user,
                Amount = transaction.Amount,
                Reference = transaction.Reference
            };
            db.PlanedTransactions.Add(trans);
            await db.SaveChangesAsync();
            return trans;
        }

        [HttpPut]
        [Route("planed/u/{userId}/t/{transactionId}/")]
        public async Task<PlanedTransaction> UpdatePlanedTransactions(string userId, int transactionId, [FromBody] ExternalTransaction transaction)
        {
            var user = await userService.GetOrCreate(userId);
            var trans = new PlanedTransaction()
            {
                Id = transactionId,
                User = user,
                Amount = transaction.Amount,
                Reference = transaction.Reference
            };
            db.PlanedTransactions.Update(trans);
            await db.SaveChangesAsync();
            return trans;
        }

        [HttpDelete]
        [Route("planed/u/{userId}/t/{transactionId}/")]
        public async Task<PlanedTransaction> DeletePlanedTransactions(string userId, int transactionId)
        {
            var userTask = userService.GetOrCreate(userId);
            var trans = await db.PlanedTransactions.Where(t => t.Id == transactionId).FirstOrDefaultAsync();
            if (trans.User != await userTask)
                throw new ApiException("this user doesn't own the given transaction");
            db.PlanedTransactions.Remove(trans);
            await db.SaveChangesAsync();
            return trans;
        }

        [HttpPost]
        [Route("send")]
        public async Task SendEvent(TransactionEvent transactionEvent)
        {

        }

        public class ExternalTransaction
        {
            public string Id { get; set; }
            public string ProductId { get; set; }
            public string Reference { get; set; }
            public decimal Amount { get; set; }
        }
    }
}
