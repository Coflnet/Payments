using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;

namespace Coflnet.Payments.Services
{
    public class GroupService
    {
        private ILogger<GroupService> logger;
        private PaymentContext db;

        public GroupService(
            ILogger<GroupService> logger,
            PaymentContext context)
        {
            this.logger = logger;
            db = context;
        }

        /// <summary>
        /// Gets the group with the given id
        /// </summary>
        /// <param name="groupId"></param>
        /// <returns></returns>
        public async Task<Group> GetGroup(string groupId)
        {
            var group = await db.Groups.Where(g => g.Slug == groupId).Include(g => g.Products).FirstOrDefaultAsync();
            if (group == null)
            {
                throw new ApiException($"Group {groupId} not found");
            }
            return group;
        }

        /// <summary>
        /// Updates the group with the given id
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="productSlugs"></param>
        /// <returns></returns>
        public async Task UpdateProductsInGroup(string groupId, string[] productSlugs)
        {
            var group = await GetGroup(groupId);
            var products = await db.Products.Where(p => productSlugs.Contains(p.Slug) || p.Slug == groupId).ToListAsync();
            group.Products = products.Select(p => (Product)p).ToList();
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Adds a new group if it doesn't exist
        /// </summary>
        /// <param name="groupId"></param>
        /// <returns></returns>
        public async Task AddOrIgnoreGroup(string groupId)
        {
            if (await db.Groups.Where(g => g.Slug == groupId).AnyAsync())
                return;
            var group = new Group() { Slug = groupId };
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            // add matching products by slug
            await UpdateProductsInGroup(groupId, new string[] { groupId });
        }
    }

}