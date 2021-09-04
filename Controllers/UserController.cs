using System;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
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

        /// <summary>
        /// Creates a new instance of <see cref="UserController"/>
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="context"></param>
        public UserController(ILogger<UserController> logger, PaymentContext context)
        {
            _logger = logger;
            db = context;
        }

        /// <summary>
        /// Creates a new user with the given id
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("{userId}")]
        public async Task<User> GetOrCreate(string userId)
        {
            var user = await db.Users.Where(u => u.ExternalId == userId).Include(u => u.Owns).FirstOrDefaultAsync();
            if (user == null)
            {
                user = new Coflnet.Payments.Models.User() { ExternalId = userId, Balance = 0 };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }
            return user;
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
            var product = await db.Products.Where(p => p.Slug == productSlug).FirstOrDefaultAsync();
            if(product.IsDisabled)
                throw new Exception("product can't be purchased");
            var user = await GetOrCreate(userId);
            if (user.Owns.Where(p => p.Product == product && p.Expires > DateTime.Now + TimeSpan.FromDays(3000)).Any())
                throw new Exception("already owned");
            if (user.Balance < product.Cost)
                throw new Exception("insuficcient balance");

            user.Balance -= product.Cost;
            user.Owns.Add(new OwnerShip() { Expires = DateTime.Now.AddSeconds(product.OwnershipSeconds), Product = product, User = user });
            db.Update(user);
            await db.SaveChangesAsync();
            return Ok();
        }
    }
}
