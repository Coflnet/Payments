using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Payments.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class RulesController
    {
        private readonly PaymentContext db;
        private readonly RuleEngine ruleEngine;

        public RulesController(PaymentContext context, RuleEngine ruleEngine)
        {
            db = context;
            this.ruleEngine = ruleEngine;
        }

        [HttpGet]
        [Route("")]
        public async Task<IEnumerable<Rule>> GetAll(int offset = 0, int amount = 20)
        {
            return await db.Rules.Skip(offset).Take(amount).ToListAsync();
        }

        [HttpGet]
        [Route("{ruleSlug}")]
        public async Task<Rule> Get(string ruleSlug)
        {
            return await db.Rules.Where(r => r.Slug == ruleSlug).FirstOrDefaultAsync();
        }

        [HttpPost]
        [Route("")]
        public async Task<Rule> CreateNew(Rule rule)
        {
            await ruleEngine.AddRule(rule);
            return await Get(rule.Slug);
        }

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
