using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Payments.Services
{
    public class UserService
    {
        private ILogger<UserService> logger;
        private PaymentContext db;

        public UserService(
            ILogger<UserService> logger,
            PaymentContext context)
        {
            this.logger = logger;
            db = context;
        }

        /// <summary>
        /// Gets or creates a user for a given id
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public async Task<User> GetOrCreate(string userId)
        {
            var user = await db.Users.Where(u => u.ExternalId == userId).Include(u => u.Owns).FirstOrDefaultAsync();
            if (user == null)
            {
                user = new Coflnet.Payments.Models.User() { ExternalId = userId, Balance = 0 };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            else
            {
                user.AvailableBalance = user.Balance + await db.PlanedTransactions.Where(t => t.User == user && t.Amount < 0).SumAsync(t => t.Amount);
            }
            return user;
        }
    }
}