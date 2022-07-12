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
        public async Task<RuleResult> GetAdjusted(Product product, User user)
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

        internal async Task AddOrUpdateRule(RuleCreate ruleCreate)
        {
            // check validity
            if (ruleCreate.TargetsGroup == null)
            {
                throw new ApiException("Rule requires a target");
            }
            var requires = db.Groups.FirstOrDefault(g => g.Slug == ruleCreate.RequiresGroup);
            if (requires == null && ruleCreate.RequiresGroup != null)
            {
                throw new ApiException("Rule requires a group that does not exist");
            }
            var targets = db.Groups.FirstOrDefault(g => g.Slug == ruleCreate.TargetsGroup);
            if (targets == null)
            {
                throw new ApiException("Rule targets a group that does not exist");
            }
            if (ruleCreate.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
            {
                if (ruleCreate.Flags.HasFlag(Rule.RuleFlags.LONGER))
                    throw new ApiException("Rule can only be LONGER or DISCOUNT, not both");
            }
            if (ruleCreate.Flags.HasFlag(Rule.RuleFlags.LONGER) || ruleCreate.Flags.HasFlag(Rule.RuleFlags.DISCOUNT))
            {
                if (ruleCreate.Amount == 0)
                    throw new ApiException("Rule requires an amount");
            }
            if (ruleCreate.Amount < 0)
                throw new ApiException("Rule amount must be positive");

            var rule = new Rule();
            var ruleFromDb = await db.Rules.FirstOrDefaultAsync(r => r.Slug == ruleCreate.Slug);
            if (ruleFromDb != null)
                rule = ruleFromDb;
            else 
                db.Rules.Add(rule);
            
            rule.Slug = ruleCreate.Slug;
            rule.Amount = ruleCreate.Amount;
            rule.Flags = ruleCreate.Flags;
            rule.Priority = ruleCreate.Priority;
            rule.Requires = requires;
            rule.Targets = targets;
            await db.SaveChangesAsync();
        }

    }

}