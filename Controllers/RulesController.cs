using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Payments.Controllers
{
    /// <summary>
    /// Manages rules for payments
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class RulesController
    {
        private readonly PaymentContext db;
        private readonly IRuleEngine ruleEngine;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ruleEngine"></param>
        public RulesController(PaymentContext context, IRuleEngine ruleEngine)
        {
            db = context;
            this.ruleEngine = ruleEngine;
        }

        /// <summary>
        /// Returns all rules
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="amount"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("")]
        public async Task<IEnumerable<Rule>> GetAll(int offset = 0, int amount = 20)
        {
            return await db.Rules.OrderBy(r=>r.Id).Skip(offset).Take(amount).ToListAsync();
        }

        /// <summary>
        /// Returns a rule by slug
        /// </summary>
        /// <param name="ruleSlug"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("{ruleSlug}")]
        public async Task<Rule> Get(string ruleSlug)
        {
            return await db.Rules.Where(r => r.Slug == ruleSlug).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Creates a new rule
        /// </summary>
        /// <param name="rule"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("")]
        public async Task<Rule> CreateNew(RuleCreate rule)
        {
            await ruleEngine.AddOrUpdateRule(rule);
            return await Get(rule.Slug);
        }

        /// <summary>
        /// Deletes a rule
        /// </summary>
        /// <param name="ruleSlug"></param>
        [HttpDelete]
        [Route("{ruleSlug}")]
        public async Task<Rule> Delete(string ruleSlug)
        {
            var rule = await Get(ruleSlug);
            db.Remove(rule);
            await db.SaveChangesAsync();
            return rule;
        }
    }
}
