using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Payments.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class GroupController
    {
        private readonly PaymentContext db;

        public GroupController(PaymentContext context)
        {
            db = context;
        }

        [HttpGet]
        [Route("")]
        public async Task<IEnumerable<Group>> GetAll(int offset = 0, int amount = 20)
        {
            return await db.Groups.OrderBy(g=>g.Id).Skip(offset).Take(amount).ToListAsync();
        }

        [HttpGet]
        [Route("{groupSlug}")]
        public async Task<Group> Get(string groupSlug)
        {
            return await db.Groups.Where(g => g.Slug == groupSlug).Include(g=>g.Products).FirstOrDefaultAsync();
        }

        [HttpPost]
        [Route("")]
        public async Task<Group> CreateNew(Group group)
        {
            db.Add(group);
            await db.SaveChangesAsync();
            return await Get(group.Slug);
        }

        [HttpPost]
        [Route("{groupSlug}/products")]
        public async Task<Group> AddProducts(string groupSlug, IEnumerable<string> productSlugs)
        {
            var group = await Get(groupSlug);
            var products = await GetProducts(productSlugs);
            foreach (var item in products)
            {
                if(group.Products.Contains(item))
                    continue;
                group.Products.Add(item);
            }
            await db.SaveChangesAsync();
            return group;
        }
        [HttpDelete]
        [Route("{groupSlug}/products")]
        public async Task<Group> RemoveProducts(string groupSlug, IEnumerable<string> productSlugs)
        {
            var group = await Get(groupSlug);
            foreach (var item in productSlugs)
            {
                var toRemove = group.Products.Where(p => p.Slug == item).FirstOrDefault();
                if(toRemove == null)
                    continue;
                group.Products.Remove(toRemove);
            }
            await db.SaveChangesAsync();
            return group;
        }

        [HttpPut]
        [Route("{groupSlug}")]
        public async Task<Group> Update(Group group)
        {
            db.Update(group);
            await db.SaveChangesAsync();
            return await Get(group.Slug);
        }

        [HttpDelete]
        [Route("{groupSlug}")]
        public async Task<Group> Delete(string groupSlug)
        {
            var group = await Get(groupSlug);
            db.Remove(group);
            await db.SaveChangesAsync();
            return group;
        }

        private async Task<IEnumerable<PurchaseableProduct>> GetProducts(IEnumerable<string> productSlugs)
        {
            var products = await db.Products.Where(p => productSlugs.Contains(p.Slug)).ToListAsync();
            return products;
        }

        private async Task<PurchaseableProduct> GetProduct(string productSlug)
        {
            return (await GetProducts(new string[] { productSlug })).FirstOrDefault();
        }
    }
}
