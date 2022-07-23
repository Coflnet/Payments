using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

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

        internal async Task ApplyGroupList(Dictionary<string, string[]> groups)
        {
            foreach (var group in groups)
            {
                await GetOrAddGroup(group.Key);
                await UpdateProductsInGroup(group.Key, group.Value);
            }
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
        /// 
        /// </summary>
        /// <param name="product"></param>
        /// <param name="groupSlug"></param>
        /// <returns></returns>
        public async Task AddProductToGroup(Product product, string groupSlug)
        {
            var group = await GetOrAddGroup(groupSlug);
            if (!group.Products.Contains(product))
                group.Products.Add(product);
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Adds a new group if it doesn't exist
        /// </summary>
        /// <param name="groupId"></param>
        /// <returns></returns>
        public async Task<Group> GetOrAddGroup(string groupId)
        {
            var group = await db.Groups.Include(g=>g.Products).Where(g => g.Slug == groupId).FirstOrDefaultAsync();
            if (group == null)
            {
                group = new Group() { Slug = groupId };
                db.Groups.Add(group);
                await db.SaveChangesAsync();
                // add matching products by slug
                await UpdateProductsInGroup(groupId, new string[] { groupId });
            }

            return group;
        }
    }

}