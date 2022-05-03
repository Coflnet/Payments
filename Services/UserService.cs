using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

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
            var user = await GetAndInclude(userId, u => u.Include(u => u.Owns));
            if (user == null)
            {
                user = new Coflnet.Payments.Models.User() { ExternalId = userId, Balance = 0 };
                db.Users.Add(user);
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (Exception e)
                {
                    if (!e.ToString().Contains("Duplicate entry"))
                        throw e;
                    return await GetAndInclude(userId, u => u.Include(u => u.Owns));
                }
            }
            else
            {
                user.AvailableBalance = user.Balance + await db.PlanedTransactions.Where(t => t.User == user && t.Amount < 0).SumAsync(t => t.Amount);
            }
            return user;
        }

        /// <summary>
        /// Get an user and include specified tables
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="includer"></param>
        /// <returns></returns>
        public async Task<User> GetAndInclude(string userId, Func<IQueryable<User>, IQueryable<User>> includer)
        {
            return await includer(db.Users.Where(u => u.ExternalId == userId)).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Returns all users that own a specific product
        /// </summary>
        /// <param name="slug"></param>
        /// <returns></returns>
        public async Task<IEnumerable<User>> GetUsersOwning(string slug)
        {
            return await db.Users.Where(u=>u.Owns.Where(o=>o.Product == db.Products.Where(p=>p.Slug == slug).FirstOrDefault() && o.Expires > DateTime.Now).Any()).ToListAsync();
        }
    }
}