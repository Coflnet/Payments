using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Payments.Models;
using Coflnet.Payments.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

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
        private readonly ProductService _productService;
        private readonly GooglePlayService _googlePlayService;
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of CreatorCodeController
        /// </summary>
        public CreatorCodeController(
            ILogger<CreatorCodeController> logger,
            CreatorCodeService creatorCodeService,
            ProductService productService,
            GooglePlayService googlePlayService,
            IConfiguration configuration)
        {
            _logger = logger;
            _creatorCodeService = creatorCodeService;
            _productService = productService;
            _googlePlayService = googlePlayService;
            _configuration = configuration;
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

        /// <summary>
        /// Gets adjusted pricing for multiple products with creator code applied (batch request)
        /// Queries localized Google Play pricing based on country code
        /// </summary>
        /// <param name="request">Batch pricing request with product slugs, country code, and optional creator code</param>
        /// <returns>Pricing information for all requested products across all payment providers</returns>
        [HttpPost("pricing/batch")]
        public async Task<ActionResult<BatchProductPricingResponse>> GetBatchProductPricing(
            [FromBody] BatchPricingRequest request)
        {
            try
            {
                if (request?.ProductSlugs == null || !request.ProductSlugs.Any())
                {
                    return BadRequest(new { error = "At least one product slug is required" });
                }

                if (string.IsNullOrWhiteSpace(request.CountryCode))
                {
                    return BadRequest(new { error = "Country code is required for localized pricing" });
                }

                // Validate creator code if provided
                CreatorCode validatedCode = null;
                if (!string.IsNullOrWhiteSpace(request.CreatorCode))
                {
                    validatedCode = await _creatorCodeService.ValidateCreatorCodeAsync(request.CreatorCode);
                    if (validatedCode == null)
                    {
                        return BadRequest(new { error = "Invalid or expired creator code" });
                    }
                }

                // Get all topup products once
                var allProducts = await _productService.GetTopupProducts();
                var packageName = _configuration["GOOGLEPAY:PackageName"];
                
                // Build response for each product
                var productPricingList = new List<ProductPricingResponse>();

                foreach (var productSlug in request.ProductSlugs.Distinct())
                {
                    var productVariants = allProducts
                        .Where(p => p.Slug == productSlug)
                        .GroupBy(p => p.ProviderSlug)
                        .ToDictionary(g => g.Key, g => g.First());

                    if (!productVariants.Any())
                    {
                        // Skip products that don't exist rather than failing the entire request
                        _logger.LogWarning("Product '{ProductSlug}' not found in batch request", productSlug);
                        continue;
                    }

                    // Build provider pricing for this product
                    var providerPricing = new List<ProviderPricingOption>();

                    foreach (var (providerSlug, product) in productVariants)
                    {
                        if (providerSlug.ToLower() == "googlepay")
                        {
                            // For Google Play, query localized pricing
                            try
                            {
                                // Determine the product ID to use - append "-5" suffix if creator code is applied
                                var googleProductId = product.Slug;
                                if (validatedCode != null)
                                {
                                    googleProductId = $"{product.Slug}-5";
                                }

                                var productDetails = await _googlePlayService.GetProductDetailsAsync(packageName, googleProductId);

                                // Extract pricing for the specified country
                                if (productDetails?.Prices != null && productDetails.Prices.ContainsKey(request.CountryCode))
                                {
                                    var priceEntry = productDetails.Prices[request.CountryCode];
                                    
                                    // Parse price information
                                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(priceEntry ?? new object());
                                    var j = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}");

                                    var priceMicros = (string)(j["priceMicros"] ?? j["priceAmountMicros"] ?? j["price_amount_micros"] ?? j["price_micro"]);
                                    var currency = (string)(j["currency"] ?? j["currencyCode"] ?? j["currency_code"]);
                                    var formatted = (string)(j["formattedPrice"] ?? j["price"] ?? j["formatted_price"]);

                                    if (long.TryParse(priceMicros, out var micros))
                                    {
                                        var price = micros / 1_000_000m;

                                        // Get title from listings if available
                                        var title = product.Title;
                                        var description = product.Description;
                                        
                                        if (productDetails.Listings != null)
                                        {
                                            // Try to get localized listing for the country, fallback to default language
                                            var listing = productDetails.Listings.ContainsKey(request.CountryCode) 
                                                ? productDetails.Listings[request.CountryCode]
                                                : productDetails.Listings.ContainsKey(productDetails.DefaultLanguage)
                                                    ? productDetails.Listings[productDetails.DefaultLanguage]
                                                    : productDetails.Listings.Values.FirstOrDefault();

                                            if (listing != null)
                                            {
                                                title = listing.Title ?? title;
                                                description = listing.Description ?? description;
                                            }
                                        }

                                        providerPricing.Add(new ProviderPricingOption
                                        {
                                            ProviderSlug = providerSlug,
                                            ProviderName = GetProviderDisplayName(providerSlug),
                                            OriginalPrice = price,
                                            DiscountedPrice = price, // Google Play handles discount via separate product
                                            DiscountAmount = 0, // Discount is built into the product price
                                            CurrencyCode = currency ?? "USD",
                                            CoinsAmount = (long)product.Cost,
                                            ProductTitle = title,
                                            ProductDescription = description,
                                            GooglePlayProductId = googleProductId
                                        });
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("Google Play pricing not available for product {ProductId} in country {Country}", 
                                        googleProductId, request.CountryCode);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to get Google Play pricing for product {ProductSlug}", product.Slug);
                                // Continue with other providers even if Google Play fails
                            }
                        }
                        else
                        {
                            // For other providers, use database pricing with calculated discount
                            var originalPrice = product.Price;
                            var discountedPrice = originalPrice;
                            var discount = 0m;

                            if (validatedCode != null)
                            {
                                discount = Math.Round(originalPrice * validatedCode.DiscountPercent / 100, 2);
                                discountedPrice = originalPrice - discount;
                            }

                            providerPricing.Add(new ProviderPricingOption
                            {
                                ProviderSlug = providerSlug,
                                ProviderName = GetProviderDisplayName(providerSlug),
                                OriginalPrice = originalPrice,
                                DiscountedPrice = discountedPrice,
                                DiscountAmount = discount,
                                CurrencyCode = product.CurrencyCode,
                                CoinsAmount = (long)product.Cost,
                                ProductTitle = product.Title,
                                ProductDescription = product.Description
                            });
                        }
                    }

                    if (providerPricing.Any())
                    {
                        productPricingList.Add(new ProductPricingResponse
                        {
                            ProductSlug = productSlug,
                            CreatorCode = validatedCode?.Code,
                            DiscountPercent = validatedCode?.DiscountPercent ?? 0,
                            Providers = providerPricing.OrderBy(p => p.DiscountedPrice).ToList()
                        });
                    }
                }

                var batchResponse = new BatchProductPricingResponse
                {
                    CreatorCode = validatedCode?.Code,
                    DiscountPercent = validatedCode?.DiscountPercent ?? 0,
                    Products = productPricingList
                };

                return Ok(batchResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get batch pricing");
                return StatusCode(500, new { error = "Failed to retrieve pricing" });
            }
        }

        /// <summary>
        /// Helper method to get user-friendly provider names
        /// </summary>
        private string GetProviderDisplayName(string providerSlug)
        {
            return providerSlug?.ToLower() switch
            {
                "stripe" => "Credit Card (Stripe)",
                "paypal" => "PayPal",
                "lemonsqueezy" => "Lemon Squeezy",
                "googlepay" => "Google Pay",
                _ => providerSlug ?? "Unknown"
            };
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

    /// <summary>
    /// Response containing pricing information for a product with creator code discount applied
    /// </summary>
    public class ProductPricingResponse
    {
        /// <summary>
        /// The product slug
        /// </summary>
        public string ProductSlug { get; set; }

        /// <summary>
        /// The creator code applied (if any)
        /// </summary>
        public string CreatorCode { get; set; }

        /// <summary>
        /// The discount percentage applied
        /// </summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>
        /// Available payment provider options with pricing
        /// </summary>
        public List<ProviderPricingOption> Providers { get; set; }
    }

    /// <summary>
    /// Pricing information for a specific payment provider
    /// </summary>
    public class ProviderPricingOption
    {
        /// <summary>
        /// The provider slug (e.g., "stripe", "paypal", "lemonsqueezy", "googlepay")
        /// </summary>
        public string ProviderSlug { get; set; }

        /// <summary>
        /// User-friendly provider name
        /// </summary>
        public string ProviderName { get; set; }

        /// <summary>
        /// Original price before discount
        /// </summary>
        public decimal OriginalPrice { get; set; }

        /// <summary>
        /// Price after creator code discount (if applicable)
        /// </summary>
        public decimal DiscountedPrice { get; set; }

        /// <summary>
        /// The discount amount in the currency
        /// </summary>
        public decimal DiscountAmount { get; set; }

        /// <summary>
        /// Currency code (e.g., "USD", "EUR")
        /// </summary>
        public string CurrencyCode { get; set; }

        /// <summary>
        /// Amount of coins/credits received
        /// </summary>
        public long CoinsAmount { get; set; }

        /// <summary>
        /// Product title
        /// </summary>
        public string ProductTitle { get; set; }

        /// <summary>
        /// Product description
        /// </summary>
        public string ProductDescription { get; set; }

        /// <summary>
        /// Google Play product ID (only for Google Play provider, includes "-5" suffix if discount applied)
        /// </summary>
        public string GooglePlayProductId { get; set; }
    }

    /// <summary>
    /// Request for batch pricing lookup
    /// </summary>
    public class BatchPricingRequest
    {
        /// <summary>
        /// List of product slugs to get pricing for
        /// </summary>
        public List<string> ProductSlugs { get; set; }

        /// <summary>
        /// Country code for localized pricing (e.g., "US", "GB", "DE")
        /// </summary>
        public string CountryCode { get; set; }

        /// <summary>
        /// Optional creator code to apply discount
        /// </summary>
        public string CreatorCode { get; set; }
    }

    /// <summary>
    /// Response containing pricing for multiple products
    /// </summary>
    public class BatchProductPricingResponse
    {
        /// <summary>
        /// The creator code applied (if any)
        /// </summary>
        public string CreatorCode { get; set; }

        /// <summary>
        /// The discount percentage applied
        /// </summary>
        public decimal DiscountPercent { get; set; }

        /// <summary>
        /// Pricing information for all requested products
        /// </summary>
        public List<ProductPricingResponse> Products { get; set; }
    }
}
