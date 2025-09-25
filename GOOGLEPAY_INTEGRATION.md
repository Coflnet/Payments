# Google Pay Integration

This document describes the Google Pay integration that has been added to the Payments service.

## Overview

The Google Pay integration allows verification and processing of in-app purchases and subscriptions made through Google Play Store. It includes:

- Purchase verification for one-time products
- Subscription verification and management
- Real-time Developer Notifications (RTDN) webhook handling
- Automatic purchase acknowledgment
- Transaction processing and payment event publishing

## Configuration

### Google Play API Setup

1. Create a service account in Google Cloud Console
2. Enable the Google Play Android Publisher API
3. Download the service account key file (JSON format)
4. Configure the application settings

### Application Configuration

Add the following configuration to your `appsettings.json`:

```json
{
  "GOOGLEPAY": {
    "PACKAGE_NAME": "your.app.package.name",
    "SERVICE_ACCOUNT_KEY_FILE": "/path/to/service-account-key.json",
    "APPLICATION_NAME": "Your Application Name",
    "PRODUCTS": {
      "coins_100": {
        "InternalProductId": 1,
        "Price": 0.99,
        "Currency": "USD"
      },
      "coins_500": {
        "InternalProductId": 2,
        "Price": 4.99,
        "Currency": "USD"
      }
    },
    "SUBSCRIPTIONS": {
      "premium_monthly": {
        "InternalProductId": 7,
        "Price": 9.99,
        "Currency": "USD"
      },
      "premium_yearly": {
        "InternalProductId": 8,
        "Price": 99.99,
        "Currency": "USD"
      }
    }
  }
}
```

## API Endpoints

### Product Purchase Verification

**POST** `/api/googlepay/verify`

Verifies and processes a Google Play product purchase.

#### Request Body

```json
{
  "productId": "coins_100",
  "purchaseToken": "purchase_token_from_google_play",
  "packageName": "your.app.package.name",
  "userId": "user_identifier",
  "customAmount": 0,
  "internalProductId": 1
}
```

#### Response

```json
{
  "isValid": true,
  "errorMessage": null,
  "transactionId": 12345,
  "purchaseState": 0
}
```

### Subscription Verification

**POST** `/api/googlepay/verify-subscription`

Verifies and processes a Google Play subscription purchase.

#### Request Body

```json
{
  "subscriptionId": "premium_monthly",
  "purchaseToken": "subscription_token_from_google_play",
  "packageName": "your.app.package.name",
  "userId": "user_identifier"
}
```

### Real-time Developer Notifications Webhook

**POST** `/callback/googlepay`

Handles Real-time Developer Notifications from Google Play.

This endpoint is automatically called by Google Play when subscription or purchase events occur.

## Services

### GooglePlayService

Handles Google Play API interactions:
- Purchase verification
- Subscription verification
- Purchase acknowledgment
- Subscription acknowledgment

### GooglePlayConfigService

Manages product mappings and configuration:
- Maps Google Play product IDs to internal product IDs
- Provides pricing information
- Handles currency mapping

## Product Mapping

The system maps Google Play product IDs to internal product IDs using the configuration. This allows:

1. **Flexible Product Management**: Change Google Play product IDs without affecting internal systems
2. **Price Configuration**: Define prices in the configuration for payment events
3. **Currency Support**: Map products to different currencies
4. **Environment Separation**: Use different product mappings for development/production

## Security

### Purchase Verification

All purchases are verified with Google Play servers before processing:

1. **Token Validation**: Purchase tokens are validated with Google Play API
2. **State Verification**: Purchase state is checked to ensure it's valid
3. **Acknowledgment Check**: Prevents processing already acknowledged purchases
4. **Duplicate Protection**: Handles duplicate transaction attempts gracefully

### Webhook Security

Real-time Developer Notifications should be configured with:

1. **HTTPS Endpoints**: Use secure endpoints for webhook callbacks
2. **Verification**: Verify webhook signatures if implementing additional security
3. **Idempotency**: Handle duplicate notifications gracefully

## Error Handling

The integration includes comprehensive error handling:

- **Invalid Purchases**: Returns appropriate error messages for invalid purchase states
- **Duplicate Transactions**: Gracefully handles already processed purchases
- **Configuration Errors**: Validates product mappings and configuration
- **API Failures**: Handles Google Play API failures with proper logging

## Monitoring and Logging

All operations are logged with appropriate severity levels:

- **Info**: Successful verifications and processing
- **Warning**: Duplicate transactions, already processed purchases
- **Error**: API failures, configuration errors, processing failures

## Integration with Existing Payment Flow

The Google Pay integration follows the same patterns as other payment providers:

1. **Transaction Service**: Uses existing `TransactionService.AddTopUp()` method
2. **Payment Events**: Publishes payment events through existing event system
3. **User Management**: Integrates with existing user management system
4. **Product Catalog**: Maps to existing internal product catalog

## Testing

### Test Purchases

Google Play provides test purchase functionality:

1. Configure test accounts in Google Play Console
2. Use test product IDs for development
3. Test purchases don't charge real money
4. Test notifications can be triggered manually

### Webhook Testing

Test Real-time Developer Notifications:

1. Use Google Play Console test notifications
2. Verify webhook endpoint receives and processes notifications correctly
3. Test various notification types (purchase, subscription, cancellation, etc.)

## Deployment

1. **Service Account**: Deploy service account key file securely
2. **Configuration**: Update configuration with production values
3. **Webhook URL**: Configure webhook URL in Google Play Console
4. **Product Mapping**: Ensure product mappings match Google Play Console setup

## Troubleshooting

### Common Issues

1. **Invalid Purchase Token**: Ensure purchase token is current and valid
2. **Package Name Mismatch**: Verify package name matches Google Play app
3. **Service Account Permissions**: Ensure service account has proper permissions
4. **Product Mapping**: Check that Google Play product IDs are correctly mapped

### Debugging

Enable detailed logging to troubleshoot issues:

- Check Google Play API responses
- Verify purchase verification results
- Monitor webhook notifications
- Validate configuration settings
