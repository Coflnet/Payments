using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;

namespace Coflnet.Payments.Services
{
    /// <summary>
    /// Applies rules to a product based on user ownership
    /// </summary>
    public class RuleEngine
    {
        private ILogger<RuleEngine> logger;
        private PaymentContext db;

        public RuleEngine(
            ILogger<RuleEngine> logger,
            PaymentContext db)
        {
            this.logger = logger;
            this.db = db;
        }

        /// <summary>
        /// Applies the rules to the given product
        /// </summary>
        /// <param name="product"></param>
        /// <param name="user"></param>
        /// <returns></returns>
        public async Task<Product> ApplyRules(Product product, User user)
        {
            var allrules = await db.Rules.ToListAsync();
            var fullUser = await db.Users.Where(u=>u.Id == user.Id).Include(u=>u.Owns).ThenInclude(o=>o.Product).ThenInclude(p=>p.Groups).ToListAsync();
            var owns = await db.Users.Where(u=>u.Id == user.Id).Include(u=>u.Owns).ThenInclude(o=>o.Product).ThenInclude(p=>p.Groups).SelectMany(u => u.Owns.SelectMany(o => o.Product.Groups)).ToListAsync();
            var groups = await db.Groups.Where(g => g.Products.Contains(product)).ToListAsync();
            var rules = await db.Rules.Where(r => owns.Contains(r.Requires) && groups.Contains(r.Targets)).ToListAsync();
            var fakeProduct = new Product(product);
            foreach (var rule in rules.OrderByDescending(r=>r.Priority))
            {
                Func<Product, decimal> applier = p => p.OwnershipSeconds;
                if(rule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
                {
                    applier = p => p.Cost;
                }
                var baseVal = applier(product);
                var change = rule.Amount;
                if(rule.Flags.HasFlag(Rule.RuleFlags.INVERT))
                    change = -change;
                if(rule.Flags.HasFlag(Rule.RuleFlags.PERCENT))
                    change = baseVal * change / 100;
                
                if(rule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
                    fakeProduct.Cost = fakeProduct.Cost - change;
                else 
                    fakeProduct.OwnershipSeconds = (long) (fakeProduct.OwnershipSeconds + change);
                
                if(rule.Flags.HasFlag(Rule.RuleFlags.EARLY_BREAK))
                    break;
            }
            return fakeProduct;
        }

        internal async Task AddRule(Rule cheaperRule)
        {
            db.Add(cheaperRule);
            await db.SaveChangesAsync();
        }
    }

}