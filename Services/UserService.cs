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
        /// <param name="create"></param>
        /// <returns></returns>
        public async Task<User> GetOrCreate(string userId, bool create = true)
        {
            var userTask = () => GetAndInclude(userId, u => u.Include(u => u.Owns).ThenInclude(o => o.Product));
            var user = await userTask();
            if(user == null && !create)
            {
                return null;
            }
            if (user == null)
            {
                user = new Coflnet.Payments.Models.User() { ExternalId = userId, Balance = 0, Owns = new() };
                db.Users.Add(user);
                try
                {
                    await db.SaveChangesAsync();
                    // select from db
                    return await userTask();
                }
                catch (Exception e)
                {
                    if (e.ToString().Contains("plicate key value violates unique constra"))
                        await GetOrCreate(userId);
                    if (!e.ToString().Contains("Duplicate entry"))
                        throw;
                    return await userTask();
                }
            }
            else
            {
                var select = db.PlanedTransactions.Where(t => t.User == user && t.Amount < 0);
                user.AvailableBalance = user.Balance + (await select.ToListAsync()).Sum(t => t.Amount);
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
        public async Task<IEnumerable<User>> GetUsersOwning(string slug, DateTime when)
        {
            var productList = await db.Groups.Where(g => g.Slug == slug).SelectMany(g => g.Products).Select(p => p.Id).ToListAsync();
            return await db.Users.Where(u => u.Owns.Where(o => productList.Contains(o.Product.Id) && o.Expires > when).Any()).ToListAsync();
        }

        internal async Task<DateTime> GetLongest(string userId, HashSet<string> slugs)
        {
            return await db.Users.Where(u => u.ExternalId == userId)
                    .SelectMany(u => u.Owns.Where(o => slugs.Contains(o.Product.Slug) || o.Product.Groups.Any(g => slugs.Contains(g.Slug)))
                    .Select(p => p.Expires)).OrderByDescending(p => p).FirstOrDefaultAsync();
        }

        // Keep GetLongest owner-only: purchase/refund calculations must not use a friend's time.
        internal IQueryable<OwnershipAccess> QueryAccess(string userId, HashSet<string> slugs, string minecraftUuid = null)
        {
            var owned = db.OwnerShips.Where(o => o.User.ExternalId == userId);
            var query = owned.Where(o => slugs.Contains(o.Product.Slug)).Select(o => new OwnershipAccess
            {
                ProductSlug = o.Product.Slug, ExpiresAt = o.Expires, OwnerId = userId, SlotId = null, MinecraftUuid = null, CanManage = true
            }).Concat(owned.SelectMany(o => o.Product.Groups.Where(g => slugs.Contains(g.Slug)),
                (o, g) => new OwnershipAccess
                { ProductSlug = g.Slug, ExpiresAt = o.Expires, OwnerId = userId, SlotId = null, MinecraftUuid = null, CanManage = true }));
            minecraftUuid = TierSlotService.NormalizeUuid(minecraftUuid);
            var slots = db.TierSlots.Where(s => s.Expires > DateTime.UtcNow
                && ((s.AssignedUserId == userId && s.MinecraftUuid == null)
                    || (minecraftUuid != null && s.MinecraftUuid == minecraftUuid
                        && (s.AssignedUserId == null || s.AssignedUserId == userId))));
            query = query.Concat(slots.Where(s => slugs.Contains(s.Tier)).Select(s => new OwnershipAccess
            {
                ProductSlug = s.Tier, ExpiresAt = s.Expires, OwnerId = s.User.ExternalId,
                SlotId = s.Id, MinecraftUuid = s.MinecraftUuid, CanManage = s.User.ExternalId == userId
            }));
            if (slugs.Contains("premium"))
                query = query.Concat(slots.Where(s => s.Tier == "premium_plus").Select(s => new OwnershipAccess
                {
                    ProductSlug = "premium", ExpiresAt = s.Expires, OwnerId = s.User.ExternalId,
                    SlotId = s.Id, MinecraftUuid = s.MinecraftUuid, CanManage = s.User.ExternalId == userId
                }));
            if (slugs.Contains("starter_premium"))
                query = query.Concat(slots.Where(s => s.Tier == "premium" || s.Tier == "premium_plus").Select(s => new OwnershipAccess
                {
                    ProductSlug = "starter_premium", ExpiresAt = s.Expires, OwnerId = s.User.ExternalId,
                    SlotId = s.Id, MinecraftUuid = s.MinecraftUuid, CanManage = s.User.ExternalId == userId
                }));
            return query.AsNoTracking();
        }

        public Task<Dictionary<string, DateTime>> GetAccessUntil(string userId, HashSet<string> slugs, string minecraftUuid = null)
            => QueryAccess(userId, slugs, minecraftUuid).GroupBy(a => a.ProductSlug)
                .Select(g => new { Slug = g.Key, Expires = g.Max(a => a.ExpiresAt) })
                .ToDictionaryAsync(a => a.Slug, a => a.Expires);

        public async Task<Dictionary<string, OwnershipAccess>> GetAccessDetails(string userId, HashSet<string> slugs, string minecraftUuid = null)
        {
            var access = await QueryAccess(userId, slugs, minecraftUuid).ToListAsync();
            return access.GroupBy(a => a.ProductSlug).ToDictionary(g => g.Key,
                g => g.OrderByDescending(a => a.ExpiresAt).ThenBy(a => a.SlotId).First());
        }
    }
}
