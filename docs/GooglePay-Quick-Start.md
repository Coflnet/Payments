# Google Pay User ID Implementation Guide

## Quick Start: Passing User ID for Purchase Credits

### Essential Code for PWA

```javascript
// 1. Purchase One-Time Products (CoflCoins)
async function buyCoflCoins(userId, productId, amount) {
    const purchaseRequest = {
        productId: productId, // Must match database slug
        developerPayload: JSON.stringify({
            user_id: userId,      // Required: Your user identifier
            custom_amount: amount // Optional: Custom coin amount
        })
    };
    
    return await billingClient.launchBillingFlow(purchaseRequest);
}

// 2. Purchase Subscriptions
async function buySubscription(userId, subscriptionId) {
    const subscriptionRequest = {
        subscriptionId: subscriptionId, // Must match database slug
        developerPayload: JSON.stringify({
            user_id: userId,           // Required: Your user identifier
            subscription_type: 'premium'
        })
    };
    
    return await billingClient.launchSubscriptionFlow(subscriptionRequest);
}
```

### Usage Examples

```javascript
// Buy 100 CoflCoins for user "player123"
buyCoflCoins('player123', 'coflcoin_100', 100);

// Buy premium subscription for user "player456"  
buySubscription('player456', 'premium_monthly');
```

### User ID Requirements

The system extracts user IDs from the `developerPayload` in this order:

1. **JSON with `user_id`** (Recommended):
   ```json
   {"user_id": "player123", "custom_amount": 100}
   ```

2. **JSON with `userId`** (Alternative):
   ```json
   {"userId": "player123", "custom_amount": 100}
   ```

3. **Direct string** (Simple):
   ```javascript
   developerPayload: "player123"
   ```

4. **Fallback**: Google's `obfuscatedExternalAccountId` (automatic)

### Product Database Configuration

Ensure your products are configured with:
- `slug` = Google Play product ID
- `providerSlug` = "googlepay"

Example:
```sql
INSERT INTO TopUpProducts (slug, providerSlug, title, price, currencyCode)
VALUES ('coflcoin_100', 'googlepay', '100 CoflCoins', 0.99, 'USD');
```

### Automatic Processing

Once configured:
1. User makes purchase with embedded user ID
2. Google Play webhook automatically credits the account
3. No additional verification needed
4. Duplicate purchases are handled automatically

### Troubleshooting

- **Credits not appearing**: Check that `user_id` is in `developerPayload`
- **Product not found**: Verify product `slug` matches Google Play product ID
- **Invalid user**: Ensure user ID exists in your system

That's it! The webhook handles everything automatically once the user ID is properly embedded in the purchase.
