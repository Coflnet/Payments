using Coflnet.Payments.Models;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

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
        public async Task<RuleResult> ApplyRules(Product product, User user)
        {
            var allrules = await db.Rules.ToListAsync();
            var owns = await db.Users.Where(u => u.Id == user.Id).Include(u => u.Owns).ThenInclude(o => o.Product).ThenInclude(p => p.Groups).SelectMany(u => u.Owns.SelectMany(o => o.Product.Groups)).ToListAsync();
            var groups = await db.Groups.Where(g => g.Products.Contains(product)).ToListAsync();
            var rules = await db.Rules.Where(r => owns.Contains(r.Requires) && groups.Contains(r.Targets)).ToListAsync();
            var fakeProduct = new Product(product);
            var appliedRules = new List<Rule>();
            foreach (var rule in rules.OrderByDescending(r => r.Priority))
            {
                Func<Product, decimal> applier = p => p.OwnershipSeconds;
                if (rule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
                {
                    applier = p => p.Cost;
                }
                var baseVal = applier(product);
                var change = rule.Amount;
                if (rule.Flags.HasFlag(Rule.RuleFlags.INVERT))
                    change = -change;
                if (rule.Flags.HasFlag(Rule.RuleFlags.PERCENT))
                    change = baseVal * change / 100;

                if (rule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
                    fakeProduct.Cost = fakeProduct.Cost - change;
                else
                    fakeProduct.OwnershipSeconds = (long)(fakeProduct.OwnershipSeconds + change);

                appliedRules.Add(rule);

                if (rule.Flags.HasFlag(Rule.RuleFlags.EARLY_BREAK))
                    break;

            }
            return new RuleResult()
            {
                ModifiedProduct = fakeProduct,
                Rules = appliedRules
            };
        }

        internal async Task AddRule(Rule cheaperRule)
        {
            // check validity
            if (cheaperRule.Targets == null)
            {
                throw new Exception("Rule requires a target");
            }
            if (cheaperRule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
            {
                if (cheaperRule.Flags.HasFlag(Rule.RuleFlags.LONGER))
                    throw new Exception("Rule can only be LONGER or DISCOUNT, not both");
            }
            if (cheaperRule.Flags.HasFlag(Rule.RuleFlags.LONGER) || cheaperRule.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
            {
                if (cheaperRule.Amount == 0)
                    throw new Exception("Rule requires an amount");
            }
            if (cheaperRule.Amount < 0)
                throw new Exception("Rule amount must be positive");

            db.Add(cheaperRule);
            await db.SaveChangesAsync();
        }
    }

}