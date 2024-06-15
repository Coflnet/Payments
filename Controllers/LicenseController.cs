using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LicenseController : ControllerBase
    {
        private readonly ILogger<LicenseController> logger;

        private LicenseService licenseService;

        public LicenseController(
            ILogger<LicenseController> logger,
            LicenseService licenseService
        )
        {
            this.logger = logger;
            this.licenseService = licenseService;
        }

        [HttpGet]
        [Route("u/{userId}")]
        public async Task<PublicLicense[]> GetLicenses(string userId)
        {
            var licenses = await licenseService.GetUserLicenses(userId);
            return licenses.Select(l => new PublicLicense(l)).ToArray();
        }

        [HttpGet]
        [Route("u/{userId}/p/{productSlug}/t/{targetId}")]
        public async Task<DateTime> HasLicenseUntil(string userId, string productSlug, string targetId)
        {
            return await licenseService.HasLicenseUntil(userId, productSlug, targetId);
        }

        [HttpPost]
        [Route("u/{userId}/p/{productSlug}/t/{targetId}")]
        public async Task PurchaseLicense(string userId, string productSlug, string targetId, string reference)
        {
            await licenseService.PurchaseLicense(userId, productSlug, targetId, reference);
        }

        [HttpGet]
        [Route("u/{userId}/t/{targetId}")]
        public async Task<PublicLicense[]> GetLicensesForTarget(string userId, string targetId)
        {
            var license = await licenseService.GetUserTargetLicenses(userId, targetId);
            return license.Select(l => new PublicLicense(l)).ToArray();
        }
    }
}
