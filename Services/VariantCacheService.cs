using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Coflnet.Payments.Services;

/// <summary>
/// Singleton service that maintains cached LemonSqueezy variant information
/// </summary>
public class VariantCacheService
{
    private readonly ILogger<VariantCacheService> logger;
    private readonly Dictionary<string, string> simpleCache = new Dictionary<string, string>();
    private readonly Dictionary<string, List<VariantInfo>> variantInfoCache = new Dictionary<string, List<VariantInfo>>();
    private readonly object cacheLock = new object();

    public VariantCacheService(ILogger<VariantCacheService> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Adds a simple variant mapping (interval key -> variant ID)
    /// </summary>
    public void AddSimpleVariant(string key, string variantId)
    {
        lock (cacheLock)
        {
            simpleCache[key] = variantId;
        }
    }

    /// <summary>
    /// Adds a variant to the enhanced cache
    /// </summary>
    public void AddVariantInfo(string intervalKey, VariantInfo variant)
    {
        lock (cacheLock)
        {
            if (!variantInfoCache.ContainsKey(intervalKey))
            {
                variantInfoCache[intervalKey] = new List<VariantInfo>();
            }
            variantInfoCache[intervalKey].Add(variant);
        }
    }

    /// <summary>
    /// Gets a simple variant ID by key
    /// </summary>
    public bool TryGetSimpleVariant(string key, out string variantId)
    {
        lock (cacheLock)
        {
            return simpleCache.TryGetValue(key, out variantId);
        }
    }

    /// <summary>
    /// Gets all variants for a given interval key
    /// </summary>
    public List<VariantInfo> GetVariants(string intervalKey)
    {
        lock (cacheLock)
        {
            if (variantInfoCache.TryGetValue(intervalKey, out var variants))
            {
                return new List<VariantInfo>(variants); // Return a copy to avoid external modifications
            }
            return null;
        }
    }

    /// <summary>
    /// Clears all cached data
    /// </summary>
    public void Clear()
    {
        lock (cacheLock)
        {
            simpleCache.Clear();
            variantInfoCache.Clear();
        }
    }

    /// <summary>
    /// Gets cache statistics
    /// </summary>
    public (int SimpleVariants, int IntervalGroups, int TotalVariants) GetStats()
    {
        lock (cacheLock)
        {
            int totalVariants = variantInfoCache.Values.Sum(v => v.Count);
            return (simpleCache.Count, variantInfoCache.Count, totalVariants);
        }
    }

    /// <summary>
    /// Get the best variant for a subscription based on duration, trial preference, and price.
    /// Prioritizes trial preference (with/without trial), then selects the best price match.
    /// Rounds ownership duration to nearest 12-hour interval to handle buffer time in subscriptions.
    /// </summary>
    public VariantInfo GetBestVariant(int ownershipSeconds, bool enableTrial, int? targetPrice = null)
    {
        // Round to nearest 12-hour interval to handle buffer time (e.g., 28 days + 3 hours should match 28 days)
        int twelveHoursInSeconds = (int)TimeSpan.FromHours(12).TotalSeconds;
        int roundedSeconds = (int)Math.Round((double)ownershipSeconds / twelveHoursInSeconds) * twelveHoursInSeconds;
        
        // Determine the interval key based on rounded ownership duration
        string intervalKey;
        int thirtyDays = (int)TimeSpan.FromDays(30).TotalSeconds;
        int ninetyDays = (int)TimeSpan.FromDays(90).TotalSeconds;
        int threeSixtyFiveDays = (int)TimeSpan.FromDays(365).TotalSeconds;
        
        // Use 3-day tolerance to handle variations like 28 days matching to 30 days (4 weeks)
        // This handles cases where products have buffer time or slightly different durations
        int tolerance = (int)TimeSpan.FromDays(3).TotalSeconds;
        
        if (Math.Abs(roundedSeconds - thirtyDays) <= tolerance)
        {
            intervalKey = "week_4";
        }
        else if (Math.Abs(roundedSeconds - ninetyDays) <= tolerance)
        {
            intervalKey = "month_3";
        }
        else if (Math.Abs(roundedSeconds - threeSixtyFiveDays) <= tolerance)
        {
            intervalKey = "year_1";
        }
        else
        {
            logger.LogWarning("Unknown ownership duration {Seconds} seconds (rounded to {RoundedSeconds}), cannot find best variant", 
                ownershipSeconds, roundedSeconds);
            return null;
        }

        // Check if we have variants for this interval
        var variants = GetVariants(intervalKey);
        if (variants == null || variants.Count == 0)
        {
            logger.LogWarning("No cached variants found for interval key {Key}", intervalKey);
            return null;
        }

        logger.LogDebug("Selecting best variant for {IntervalKey} with enableTrial={EnableTrial}, targetPrice={TargetPrice}. Found {Count} variants",
            intervalKey, enableTrial, targetPrice, variants.Count);

        // Step 1: Filter by trial preference
        var matchingTrialVariants = variants.Where(v => v.HasFreeTrial == enableTrial).ToList();
        
        // Step 2: Select best price match, with fallback logic
        VariantInfo bestVariant = null;
        
        if (targetPrice.HasValue && targetPrice.Value > 0)
        {
            // Try to find best match in trial-filtered variants first
            if (matchingTrialVariants.Count > 0)
            {
                var exactMatch = matchingTrialVariants.FirstOrDefault(v => v.Price == targetPrice.Value);
                if (exactMatch != null)
                {
                    bestVariant = exactMatch;
                }
                else
                {
                    var lowerOrEqual = matchingTrialVariants
                        .Where(v => v.Price <= targetPrice.Value)
                        .OrderByDescending(v => v.Price)
                        .FirstOrDefault();
                    
                    if (lowerOrEqual != null)
                    {
                        bestVariant = lowerOrEqual;
                    }
                }
            }

            // If no good match in trial-filtered variants, fallback to ALL variants
            if (bestVariant == null)
            {
                logger.LogInformation("No good price match with HasFreeTrial={EnableTrial} for {IntervalKey}, trying all variants", 
                    enableTrial, intervalKey);
                
                var exactMatch = variants.FirstOrDefault(v => v.Price == targetPrice.Value);
                if (exactMatch != null)
                {
                    bestVariant = exactMatch;
                }
                else
                {
                    var lowerOrEqual = variants
                        .Where(v => v.Price <= targetPrice.Value)
                        .OrderByDescending(v => v.Price)
                        .FirstOrDefault();
                    
                    if (lowerOrEqual != null)
                    {
                        bestVariant = lowerOrEqual;
                    }
                    else
                    {
                        // No variants match target price, pick lowest available
                        bestVariant = variants.OrderBy(v => v.Price).First();
                    }
                }
            }
        }
        else
        {
            // No target price - prefer trial-matching variants, fallback to all
            if (matchingTrialVariants.Count > 0)
            {
                bestVariant = matchingTrialVariants.OrderBy(v => v.Price).First();
            }
            else
            {
                logger.LogInformation("No variants with HasFreeTrial={EnableTrial} found for {IntervalKey}, using all variants", 
                    enableTrial, intervalKey);
                bestVariant = variants.OrderBy(v => v.Price).First();
            }
        }

        logger.LogInformation("Selected variant: {VariantName} (ID: {VariantId}) Price: {Price} HasTrial: {HasTrial} for {IntervalKey}",
            bestVariant.VariantName, bestVariant.VariantId, bestVariant.Price, bestVariant.HasFreeTrial, intervalKey);

        return bestVariant;
    }
}

/// <summary>
/// Enhanced variant info stored in cache
/// </summary>
public class VariantInfo
{
    public string VariantId { get; set; }
    public string ProductName { get; set; }
    public string VariantName { get; set; }
    public int Price { get; set; }
    public bool HasFreeTrial { get; set; }
    public string Interval { get; set; }
    public int IntervalCount { get; set; }
    public bool IsSubscription { get; set; }
    public int ProductId { get; set; }
}
