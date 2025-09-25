# Google Pay Integration Guide for PWA

This guide explains how to integrate Google Pay In-App Billing into your Progressive Web App (PWA) and how to properly pass user IDs for transaction crediting.

## Overview

The payment system supports Google Play In-App Billing through two main endpoints:
- `/callback/googlepay/verify` - For manual purchase verification
- `/callback/googlepay` - Webhook for Real-time Developer Notifications (RTDN)

## Prerequisites

1. Google Play Console setup with In-App Products and Subscriptions
2. Google Play Billing Library integrated in your PWA
3. Real-time Developer Notifications configured in Google Play Console

## PWA Implementation

### 1. Initialize Google Play Billing

```javascript
// Initialize the Google Play Billing client
const billingClient = new google.payments.api.PaymentsClient({
    environment: 'PRODUCTION', // or 'TEST' for testing
    merchantInfo: {
        merchantId: 'your-merchant-id',
        merchantName: 'Your App Name'
    }
});
```

### 2. Purchase One-Time Products

For one-time product purchases (CoflCoins), include the user ID in the `developerPayload`:

```javascript
async function purchaseProduct(productId, userId, customAmount = null) {
    try {
        const purchaseRequest = {
            productId: productId,
            developerPayload: JSON.stringify({
                user_id: userId,
                custom_amount: customAmount || 0,
                timestamp: Date.now()
            })
        };

        const purchaseResult = await billingClient.launchBillingFlow(purchaseRequest);
        
        if (purchaseResult.responseCode === BillingResponseCode.OK) {
            // Purchase initiated successfully
            console.log('Purchase initiated:', purchaseResult);
            
            // The webhook will automatically process the purchase
            // when Google sends the notification
            return purchaseResult;
        } else {
            throw new Error(`Purchase failed: ${purchaseResult.responseCode}`);
        }
    } catch (error) {
        console.error('Purchase error:', error);
        throw error;
    }
}

// Example usage
purchaseProduct('coflcoin_100', 'user123', 100);
```

### 3. Purchase Subscriptions

For subscription purchases, use similar approach:

```javascript
async function purchaseSubscription(subscriptionId, userId) {
    try {
        const subscriptionRequest = {
            subscriptionId: subscriptionId,
            developerPayload: JSON.stringify({
                user_id: userId,
                subscription_type: 'premium',
                timestamp: Date.now()
            })
        };

        const subscaseResult = await billingClient.launchSubscriptionFlow(subscriptionRequest);
        
        if (subscaseResult.responseCode === BillingResponseCode.OK) {
            console.log('Subscription initiated:', subscaseResult);
            return subscaseResult;
        } else {
            throw new Error(`Subscription failed: ${subscaseResult.responseCode}`);
        }
    } catch (error) {
        console.error('Subscription error:', error);
        throw error;
    }
}

// Example usage
purchaseSubscription('premium_monthly', 'user123');
```

### 4. Manual Verification (Optional)

If you need to manually verify purchases (in addition to automatic webhook processing):

```javascript
async function verifyPurchase(purchaseToken, productId, packageName, userId, customAmount = null) {
    try {
        const response = await fetch('/callback/googlepay/verify', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({
                purchaseToken: purchaseToken,
                productId: productId,
                packageName: packageName,
                userId: userId,
                customAmount: customAmount
            })
        });

        const result = await response.json();
        
        if (result.isValid) {
            console.log('Purchase verified and credited');
        } else {
            console.error('Purchase verification failed:', result.errorMessage);
        }
        
        return result;
    } catch (error) {
        console.error('Verification request failed:', error);
        throw error;
    }
}
```

## User ID Passing Strategies

The system extracts user IDs from purchases using these methods (in order of priority):

### 1. DeveloperPayload (Recommended)

Pass user ID in the `developerPayload` as JSON:

```javascript
const developerPayload = JSON.stringify({
    user_id: "user123",           // Primary user identifier
    custom_amount: 100,           // Optional custom amount
    metadata: {                   // Optional additional data
        source: "web_app",
        campaign: "summer_sale"
    }
});
```

### 2. Alternative DeveloperPayload Formats

If JSON parsing fails, the system also supports:

```javascript
// Direct user ID (simple string)
const developerPayload = "user123";

// Alternative JSON key
const developerPayload = JSON.stringify({
    userId: "user123"  // Alternative key name
});
```

### 3. Fallback: ObfuscatedAccountId

If `developerPayload` is empty, the system will use Google's `obfuscatedExternalAccountId` if available.

## Product Configuration

### Database Setup

Products must be configured in the database with:

- `slug`: Must match the Google Play product ID (SKU)
- `providerSlug`: Set to "googlepay"
- `price`: Product price in the specified currency
- `currencyCode`: Currency code (e.g., "USD", "EUR")

Example database entry:
```sql
INSERT INTO TopUpProducts (slug, providerSlug, title, price, currencyCode, description, type)
VALUES ('coflcoin_100', 'googlepay', '100 CoflCoins', 0.99, 'USD', '100 CoflCoins pack', 1);
```

### Google Play Console Setup

1. Create In-App Products in Google Play Console
2. Set Product ID to match your database `slug`
3. Configure pricing and availability
4. Activate the products

## Webhook Configuration

### Real-time Developer Notifications

1. In Google Play Console, go to Monetization → Real-time developer notifications
2. Set webhook URL to: `https://your-domain.com/callback/googlepay`
3. Configure notification types:
   - One-time product notifications
   - Subscription notifications

### Webhook Security

The webhook endpoint automatically processes notifications from Google Play. No additional authentication is required as Google Play handles webhook verification.

## Error Handling

### Common Issues and Solutions

1. **User ID Not Found**
   - Ensure `developerPayload` contains valid JSON with `user_id`
   - Check that the user ID exists in your system

2. **Product Not Found**
   - Verify the Google Play product ID matches the database `slug`
   - Ensure `providerSlug` is set to "googlepay"

3. **Duplicate Transactions**
   - The system automatically handles duplicate notifications
   - Purchases are idempotent based on Google's order ID

4. **Purchase Already Acknowledged**
   - The webhook skips already processed purchases
   - No action needed for duplicate notifications

## Testing

### Test Environment

1. Use Google Play's test environment
2. Add test accounts in Google Play Console
3. Use test product IDs for development

### Test Purchase Flow

```javascript
// Test purchase with logging
async function testPurchase() {
    try {
        const result = await purchaseProduct('test_product_100', 'test_user_123', 100);
        console.log('Test purchase result:', result);
        
        // Monitor webhook logs for processing confirmation
        console.log('Check server logs for webhook processing');
    } catch (error) {
        console.error('Test purchase failed:', error);
    }
}
```

## Security Considerations

1. **User ID Validation**: Always validate user IDs server-side
2. **Amount Verification**: Verify custom amounts against business rules
3. **Rate Limiting**: Implement purchase rate limiting to prevent abuse
4. **Fraud Detection**: Monitor for suspicious purchase patterns

## Monitoring and Logging

The system provides comprehensive logging for:
- Purchase verification attempts
- User ID extraction success/failure
- Product lookup results
- Transaction processing status
- Webhook notification handling

Monitor these logs for troubleshooting and fraud detection.

## Support and Troubleshooting

If you encounter issues:

1. Check server logs for detailed error messages
2. Verify Google Play Console configuration
3. Ensure database products are properly configured
4. Test with Google Play's sandbox environment
5. Verify webhook endpoint accessibility

## Example Complete Integration

```html
<!DOCTYPE html>
<html>
<head>
    <title>CoflCoin Purchase</title>
    <script src="https://pay.google.com/gp/p/js/pay.js"></script>
</head>
<body>
    <button onclick="buyCoflCoins()">Buy 100 CoflCoins</button>
    
    <script>
        const billingClient = new google.payments.api.PaymentsClient({
            environment: 'TEST' // Change to 'PRODUCTION' for live
        });

        async function buyCoflCoins() {
            const userId = getCurrentUserId(); // Your user identification logic
            const productId = 'coflcoin_100';
            const customAmount = 100;

            try {
                await purchaseProduct(productId, userId, customAmount);
                alert('Purchase initiated! Your coins will be credited shortly.');
            } catch (error) {
                alert('Purchase failed: ' + error.message);
            }
        }

        function getCurrentUserId() {
            // Implement your user identification logic here
            return 'user123'; // Example
        }

        async function purchaseProduct(productId, userId, customAmount) {
            const purchaseRequest = {
                productId: productId,
                developerPayload: JSON.stringify({
                    user_id: userId,
                    custom_amount: customAmount,
                    timestamp: Date.now()
                })
            };

            return await billingClient.launchBillingFlow(purchaseRequest);
        }
    </script>
</body>
</html>
```

This integration ensures that:
- User purchases are properly tracked and attributed
- CoflCoins are credited to the correct user account
- Subscriptions are managed automatically
- The system handles edge cases and errors gracefully
