using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Payments.Controllers
{
    /// <summary>
    /// Controller for managing creator codes and revenue tracking
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class CreatorCodeController : ControllerBase
    {
        private readonly ILogger<CreatorCodeController> _logger;
        private readonly CreatorCodeService _creatorCodeService;

        /// <summary>
        /// Initializes a new instance of CreatorCodeController
        /// </summary>
        public CreatorCodeController(
            ILogger<CreatorCodeController> logger,
            CreatorCodeService creatorCodeService)
        {
            _logger = logger;
            _creatorCodeService = creatorCodeService;
        }

        /// <summary>
        /// Creates a new creator code
        /// </summary>
        /// <param name="request">The creator code creation request</param>
        /// <returns>The created creator code</returns>
        [HttpPost]
        public async Task<ActionResult<CreatorCode>> CreateCreatorCode([FromBody] CreateCreatorCodeRequest request)
        {
            try
            {
                var creatorCode = await _creatorCodeService.CreateCreatorCodeAsync(
                    request.Code,
                    request.CreatorUserId,
                    request.DiscountPercent,
                    request.RevenueSharePercent,
                    request.ExpiresAt,
                    request.MaxUses
                );

                return Ok(creatorCode);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create creator code");
                return StatusCode(500, new { error = "Failed to create creator code" });
            }
        }

        /// <summary>
        /// Gets a creator code by code string
        /// </summary>
        /// <param name="code">The code to retrieve</param>
        /// <returns>The creator code</returns>
        [HttpGet("{code}")]
        public async Task<ActionResult<CreatorCode>> GetCreatorCode(string code)
        {
            var creatorCode = await _creatorCodeService.GetCreatorCodeAsync(code);

            if (creatorCode == null)
            {
                return NotFound(new { error = $"Creator code '{code}' not found" });
            }

            return Ok(creatorCode);
        }

        /// <summary>
        /// Validates a creator code
        /// </summary>
        /// <param name="code">The code to validate</param>
        /// <returns>The creator code if valid</returns>
        [HttpGet("validate/{code}")]
        public async Task<ActionResult<CreatorCodeValidationResponse>> ValidateCreatorCode(string code)
        {
            var creatorCode = await _creatorCodeService.ValidateCreatorCodeAsync(code);

            if (creatorCode == null)
            {
                return Ok(new CreatorCodeValidationResponse
                {
                    IsValid = false,
                    Message = "Creator code is invalid, expired, or has reached maximum uses"
                });
            }

            return Ok(new CreatorCodeValidationResponse
            {
                IsValid = true,
                DiscountPercent = creatorCode.DiscountPercent,
                Code = creatorCode.Code,
                Message = $"Valid! Get {creatorCode.DiscountPercent}% off"
            });
        }

        /// <summary>
        /// Gets revenue report for a creator code in a specific time period
        /// </summary>
        /// <param name="code">The creator code</param>
        /// <param name="startDate">Start date (ISO 8601)</param>
        /// <param name="endDate">End date (ISO 8601)</param>
        /// <returns>Revenue report</returns>
        [HttpGet("{code}/revenue")]
        public async Task<ActionResult<CreatorRevenueReport>> GetRevenueReport(
            string code,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            try
            {
                var start = startDate ?? DateTime.UtcNow.AddMonths(-1);
                var end = endDate ?? DateTime.UtcNow;

                var report = await _creatorCodeService.GetRevenueReportAsync(code, start, end);

                return Ok(report);
            }
            catch (ArgumentException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get revenue report for code {Code}", code);
                return StatusCode(500, new { error = "Failed to retrieve revenue report" });
            }
        }

        /// <summary>
        /// Updates a creator code
        /// </summary>
        /// <param name="code">The code to update</param>
        /// <param name="request">The update request</param>
        /// <returns>The updated creator code</returns>
        [HttpPut("{code}")]
        public async Task<ActionResult<CreatorCode>> UpdateCreatorCode(
            string code,
            [FromBody] UpdateCreatorCodeRequest request)
        {
            try
            {
                var creatorCode = await _creatorCodeService.UpdateCreatorCodeAsync(
                    code,
                    request.DiscountPercent,
                    request.RevenueSharePercent,
                    request.IsActive,
                    request.ExpiresAt,
                    request.MaxUses
                );

                return Ok(creatorCode);
            }
            catch (ArgumentException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update creator code {Code}", code);
                return StatusCode(500, new { error = "Failed to update creator code" });
            }
        }

        /// <summary>
        /// Gets all creator codes for a specific creator
        /// </summary>
        /// <param name="userId">The creator's user ID</param>
        /// <returns>List of creator codes</returns>
        [HttpGet("user/{userId}")]
        public async Task<ActionResult<List<CreatorCode>>> GetCreatorCodesForUser(string userId)
        {
            try
            {
                var codes = await _creatorCodeService.GetCreatorCodesForUserAsync(userId);
                return Ok(codes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get creator codes for user {UserId}", userId);
                return StatusCode(500, new { error = "Failed to retrieve creator codes" });
            }
        }
    }

    /// <summary>
    /// Request to create a new creator code
    /// </summary>
    public class CreateCreatorCodeRequest
    {
        /// <summary>
        /// The code string (e.g., "TECHNO")
        /// </summary>
        public string Code { get; set; }

        /// <summary>
        /// The creator's user ID
        /// </summary>
        public string CreatorUserId { get; set; }

        /// <summary>
        /// Discount percentage (e.g., 5 for 5%)
        /// </summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>
        /// Revenue share percentage for the creator (e.g., 5 for 5%)
        /// </summary>
        public decimal RevenueSharePercent { get; set; }

        /// <summary>
        /// Optional expiration date
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Optional maximum number of uses
        /// </summary>
        public int? MaxUses { get; set; }
    }

    /// <summary>
    /// Request to update a creator code
    /// </summary>
    public class UpdateCreatorCodeRequest
    {
        /// <summary>
        /// New discount percentage
        /// </summary>
        public decimal? DiscountPercent { get; set; }

        /// <summary>
        /// New revenue share percentage
        /// </summary>
        public decimal? RevenueSharePercent { get; set; }

        /// <summary>
        /// New active status
        /// </summary>
        public bool? IsActive { get; set; }

        /// <summary>
        /// New expiration date
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// New max uses
        /// </summary>
        public int? MaxUses { get; set; }
    }

    /// <summary>
    /// Response for creator code validation
    /// </summary>
    public class CreatorCodeValidationResponse
    {
        /// <summary>
        /// Whether the code is valid
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// The discount percentage if valid
        /// </summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>
        /// The normalized code
        /// </summary>
        public string Code { get; set; }

        /// <summary>
        /// A message about the validation result
        /// </summary>
        public string Message { get; set; }
    }
}
