using System;
using System.Collections.Generic;
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
            return await db.OwnerShips.Where(o=>o.User.ExternalId == userId && o.Product.Slug == productSlug)
                .Select(o=>o.Expires).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Returns the bigest time out of a list of product ids
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="slugs"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/owns/longest")]
        public async Task<DateTime> GetLongest(string userId, [FromBody] HashSet<string> slugs)
        {
            return await userService.GetLongest(userId, slugs);
        }
        /// <summary>
        /// Returns all ownership data for an user out of a list of interested 
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="slugs"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/owns")]
        [Obsolete("lookup with {userId}/owns/until")]
        public async Task<IEnumerable<OwnerShip>> GetAllOwnerships(string userId, [FromBody] HashSet<string> slugs)
        {
            var user = await GetOrCreate(userId);
            var select = db.Users.Where(u => u.ExternalId == userId)
                    .Include(p => p.Owns).ThenInclude(o => o.Product)
                    .SelectMany(u => u.Owns.Where(o => slugs.Contains(o.Product.Slug) || o.Product.Groups.Any(g => slugs.Contains(g.Slug))));
            return await select.ToListAsync();
        }

        /// <summary>
        /// Returns all ownership data for an user out of a list of interested 
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="slugs"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/owns/until")]
        public async Task<Dictionary<string, DateTime>> GetAllOwnershipsLookup(string userId, [FromBody] HashSet<string> slugs)
        {
            var user = await GetOrCreate(userId);
            var select = db.Users.Where(u => u.ExternalId == userId)
                    .SelectMany(u => u.Owns.Where(o => slugs.Contains(o.Product.Slug) || o.Product.Groups.Any(g => slugs.Contains(g.Slug))))
                    .SelectMany(p => p.Product.Groups, (o, group) => new { o.Expires, group.Slug })
                    .AsNoTracking();
            var result = await select.ToListAsync();
            return result.GroupBy(r => r.Slug)
                .Select(g => g.OrderByDescending(r => r.Expires).First())
                .Where(r => slugs.Contains(r.Slug))
                .ToDictionary(r => r.Slug, r => r.Expires);
        }

        /// <summary>
        /// Purchase a new product if enough funds are available
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productSlug"></param>
        /// <param name="price"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/purchase/{productSlug}")]
        public async Task<ActionResult<User>> Purchase(string userId, string productSlug, int price = 0)
        {
            await transactionService.PurchaseProduct(productSlug, userId, price);
            return Ok();
        }

        /// <summary>
        /// Purchase/extends a service if enough funds are available
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="productSlug"></param>
        /// <param name="reference"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/service/purchase/{productSlug}")]
        public async Task<ActionResult<User>> PurchaseService(string userId, string productSlug, string reference, int count = 1)
        {
            await transactionService.PurchaseServie(productSlug, userId, count, reference);
            return Ok();
        }

        /// <summary>
        /// Undo the purchase of a service
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="transactionId"></param>
        /// <returns></returns>
        [HttpDelete]
        [Route("{userId}/{transactionId}")]
        public async Task<TransactionEvent> RevertServicePUrchase(string userId, int transactionId)
        {
            return await transactionService.RevertPurchase(userId, transactionId);
        }

        /// <summary>
        /// Transfers coins to another user
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}/transfer")]
        public async Task<TransactionEvent> Transfer(string userId, [FromBody] TransferRequest request)
        {
            return await transactionService.Transfer(userId, request.TargetUser, (decimal)request.Amount, request.Reference);
        }
    }
}
