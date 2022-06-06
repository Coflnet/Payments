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
        public async Task ApplyRules(Product product, User user)
        {
            var owns = await db.Users.SelectMany(u => u.Owns.SelectMany(o => o.Product.Groups)).ToListAsync();
            Console.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(owns, Newtonsoft.Json.Formatting.Indented));
            var groups = await db.Groups.Where(g => g.Products.Contains(product)).ToListAsync();
            var rules = await db.Rules.Where(r => owns.Contains(r.Requires) && groups.Contains(r.Targets)).ToListAsync();
            foreach (var rule in rules.OrderByDescending(r=>r.Priority))
            {
                Console.WriteLine("applying rule " + rule.Slug);
            }
        }
    }

}