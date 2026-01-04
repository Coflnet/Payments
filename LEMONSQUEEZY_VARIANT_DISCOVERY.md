# LemonSqueezy Variant Auto-Discovery and Quarterly Subscriptions

## Overview
This update adds automatic product variant discovery from LemonSqueezy and support for quarterly (3-month) subscriptions as a middle-ground option between monthly and yearly plans.

## Changes Made

### 1. Configuration Updates
**File: `appsettings.json`**
- Added `QUARTER_SUBSCRIPTION_VARIANT_ID` configuration between monthly and yearly variants
- This allows for quarterly subscription option to be configured

### 2. New API Models
**File: `Models/LemonSqueezy.cs`**
Added new model classes for LemonSqueezy API responses:
- `ProductListResponse` - Response wrapper for product list API
- `ProductData` - Individual product data
- `ProductAttributes` - Product attributes (name, slug, status)
- `VariantListResponse` - Response wrapper for variant list API
- `VariantData` - Individual variant data
- `VariantAttributes` - Variant details (interval, price, subscription info)
- `MetaData` / `PageMetaInfo` - Pagination metadata

### 3. LemonSqueezy Service Enhancements
**File: `Services/LemonSqueezyService.cs`**

#### New Features:
- **Variant Caching**: Added `variantCache` dictionary to cache discovered variant IDs
- **`DiscoverVariantsAsync()`**: New method that:
  - Fetches all published products from the configured store
  - Retrieves variants for each product
  - Caches subscription variants by interval type (week_4, month_3, year_1)
  - Caches regular (non-subscription) variants
  - Logs discovery progress for debugging

- **`GetVariantId(int ownershipSeconds)`**: Smart variant ID resolver that:
  - Checks cache first for dynamically discovered variants
  - Falls back to configuration values if not in cache
  - Supports monthly (30 days), quarterly (90 days), and yearly (365 days) subscriptions

- **Discount Validation Update**: Updated `ValidateDiscountCodeAsync()` to include quarterly subscription variant ID when checking if discounts are subscription-only

### 4. Startup Initialization
**File: `Services/LemonSqueezyInitializationService.cs`** (NEW)
- Created background hosted service that runs variant discovery on application startup
- Uses scoped service provider to get LemonSqueezyService instance
- Gracefully handles failures - won't crash the application if discovery fails
- Logs all discovery activities for monitoring

**File: `Startup.cs`**
- Registered `LemonSqueezyInitializationService` as a hosted service
- Runs automatically when the application starts

### 5. Controller Updates
**File: `Controllers/TopUpController.cs`**
- **`LemonSqueezySubscribe()` method**: Refactored to use the new `GetVariantId()` method instead of hardcoded configuration lookups
- Now automatically selects the correct variant based on product's `OwnershipSeconds` property
- Supports quarterly subscriptions automatically if a 90-day product is configured

## How It Works

### Startup Flow:
1. Application starts
2. `LemonSqueezyInitializationService` executes
3. Calls `DiscoverVariantsAsync()` on LemonSqueezy service
4. Fetches products from store via API
5. For each product, fetches its variants
6. Caches subscription variants by interval pattern
7. Logs discovered variants for verification

### Runtime Flow:
1. User requests subscription for a product
2. `TopUpController.LemonSqueezySubscribe()` is called
3. Product's `OwnershipSeconds` determines subscription length
4. `GetVariantId()` looks up appropriate variant:
   - First checks cache for discovered variants
   - Falls back to configuration if not found
5. Correct LemonSqueezy variant ID is used for checkout

### Supported Subscription Lengths:
- **Monthly**: 30 days (4 weeks interval in LemonSqueezy)
- **Quarterly**: 90 days (3 months interval in LemonSqueezy) - NEW!
- **Yearly**: 365 days (1 year interval in LemonSqueezy)

## Testing

### API Testing (Already Done):
Used curl to verify LemonSqueezy API responses:
```bash
# Fetch products
curl -g -H "Authorization: Bearer $API_KEY" \
  'https://api.lemonsqueezy.com/v1/products?filter[store_id]=12595'

# Fetch variants for a product
curl -g -H "Authorization: Bearer $API_KEY" \
  'https://api.lemonsqueezy.com/v1/products/{product_id}/variants'
```

### Results:
- Monthly premium (product 341744) → variant 502893 (4 weeks interval)
- Yearly premium (product 341747) → variant 502900 (1 year interval)

### To Add Quarterly Subscription:
1. Create a new subscription product in LemonSqueezy dashboard with 3-month interval
2. The variant will be automatically discovered on next startup
3. Create a product in your database with `OwnershipSeconds = 7776000` (90 days)
4. The system will automatically map it to the quarterly variant

## Configuration

### User Secrets:
The application uses the following LemonSqueezy configuration (from user secrets):
```
LEMONSQUEEZY:API_KEY = <api_key>
LEMONSQUEEZY:STORE_ID = 12595
LEMONSQUEEZY:SUBSCRIPTION_VARIANT_ID = 502893 (monthly)
LEMONSQUEEZY:YEAR_SUBSCRIPTION_VARIANT_ID = 502900 (yearly)
LEMONSQUEEZY:QUARTER_SUBSCRIPTION_VARIANT_ID = <to_be_configured>
```

### Fallback Behavior:
If variant discovery fails or a variant isn't found in cache:
- Monthly subscriptions fall back to `SUBSCRIPTION_VARIANT_ID`
- Quarterly subscriptions fall back to `QUARTER_SUBSCRIPTION_VARIANT_ID`
- Yearly subscriptions fall back to `YEAR_SUBSCRIPTION_VARIANT_ID`

## Benefits

1. **Automatic Discovery**: No need to manually update variant IDs in configuration when products change
2. **Flexibility**: Easy to add new subscription lengths by creating products in LemonSqueezy
3. **Middle Ground Option**: Quarterly subscriptions provide a pricing tier between monthly and yearly
4. **Maintainability**: Reduces hardcoded configuration dependencies
5. **Resilience**: Falls back to configuration if discovery fails

## Monitoring

Check application logs on startup for:
```
Starting variant discovery for store {StoreId}
Found {Count} products
Discovered variant: {ProductName} - {Interval} x {Count} = ID {VariantId}
Variant discovery complete. Cached {Count} variants
```

## Future Enhancements

Possible improvements:
1. Add periodic refresh of variant cache (every 24 hours)
2. Add admin endpoint to manually trigger variant discovery
3. Cache variant data in Redis for multi-instance deployments
4. Add metrics/telemetry for variant discovery success/failure
5. Support custom interval patterns beyond standard monthly/quarterly/yearly
