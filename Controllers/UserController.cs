using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// Handles users
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class UserController : ControllerBase
    {
        private readonly ILogger<UserController> _logger;
        private readonly PaymentContext db;
        private readonly TransactionService transactionService;
        private readonly UserService userService;

        /// <summary>
        /// Creates a new instance of <see cref="UserController"/>
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="context"></param>
        /// <param name="transactionService"></param>
        /// <param name="userService"></param>
        public UserController(ILogger<UserController> logger, 
            PaymentContext context,
            TransactionService transactionService,
            UserService userService)
        {
            _logger = logger;
            db = context;
            this.transactionService = transactionService;
            this.userService = userService;
        }

        /// <summary>
        /// Creates a new user with the given id
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}")]
        public Task<User> GetOrCreate(string userId)
        {
            return userService.GetOrCreate(userId);
        }

        /// <summary>
        /// Gets the user with the given id
        /// </summary>
        /// <param name="userId"></param>
        /// <returns>The user or 204 (no content)</returns>
        [HttpGet]
        [Route("{userId}")]
        public Task<User> Get(string userId)
        {
            return GetOrCreate(userId);
        }

        /// <summary>
        /// Returns the time for how long a user owns a given product
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productSlug"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("{userId}/owns/{productSlug}/until")]
        public async Task<DateTime> Get(string userId, string productSlug)
        {
            var user = await GetOrCreate(userId);
            return await db.Users.Where(u => u.ExternalId == userId)
                    .Select(u => u.Owns.Where(o => o.Product == db.Products.Where(p => p.Slug == productSlug))
                    .Select(p => p.Expires).FirstOrDefault()).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Purchase a new product if enough funds are available
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productSlug"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/purchase/{productSlug}")]
        public async Task<ActionResult<User>> Purchase(string userId, string productSlug)
        {
            await transactionService.PurchaseProduct(productSlug,userId);
            return Ok();
        }
    }
}
