using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;

namespace Payments.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ApplyController : ControllerBase
    {
        private readonly PaymentContext db;
        private readonly ProductService productService;
        private readonly GroupService groupService;
        private readonly RuleEngine ruleEngine;

        public ApplyController(PaymentContext db, ProductService productService, GroupService groupService, RuleEngine ruleEngine)
        {
            this.db = db;
            this.productService = productService;
            this.groupService = groupService;
            this.ruleEngine = ruleEngine;
        }

        /// <summary>
        /// Brings all products, groups and roles into the given state
        /// will disable/delete anything not present so use carefully
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        /// <exception cref="ApiException"></exception>
        [HttpPost]
        public async Task ApplyState(SystemState state)
        {
            if(state.Products == null || state.Products.Count == 0)
                throw new ApiException("No products provided, this is likely an mistake, blocking applying of state");
            await productService.ApplyProductList(state.Products);
            await productService.ApplyTopupList(state.TopUpProducts);
            await groupService.ApplyGroupList(state.Groups);
            await ruleEngine.ApplyRuleList(state.Rules);
        }
    }
}
