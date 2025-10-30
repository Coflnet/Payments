# Creator Code System

The creator code system allows users to apply discount codes when making purchases, which also attributes the purchase to a content creator and tracks revenue share for payouts.

## Features

- **Dynamic Discounts**: Support for configurable discount percentages (e.g., 5% or 10% for special occasions)
- **Revenue Attribution**: Tracks all purchases made with creator codes
- **Revenue Sharing**: Configurable revenue share percentage for creators
- **Usage Limits**: Optional max uses and expiration dates
- **Multi-Currency Support**: Tracks revenue in multiple currencies
- **Payout Tracking**: Mark revenue as paid out to creators

## Database Schema

### CreatorCodes Table
- `Id`: Primary key
- `Code`: Unique code string (e.g., "TECHNO")
- `CreatorUserId`: The creator's user ID
- `DiscountPercent`: Discount percentage for users (e.g., 5 for 5%)
- `RevenueSharePercent`: Revenue share percentage for creator
- `IsActive`: Whether the code is currently active
- `CreatedAt`, `UpdatedAt`: Timestamps
- `ExpiresAt`: Optional expiration date
- `MaxUses`: Optional maximum usage limit
- `TimesUsed`: Current usage counter

### CreatorCodeRevenues Table
- `Id`: Primary key
- `CreatorCodeId`: Foreign key to CreatorCodes
- `UserId`: The purchasing user
- `ProductId`: The purchased product
- `OriginalPrice`: Price before discount
- `DiscountAmount`: Discount applied
- `FinalPrice`: Final price paid
- `CreatorRevenue`: Revenue share for creator
- `Currency`: Currency code (ISO 4217)
- `TransactionReference`: Payment provider transaction ID
- `PurchasedAt`: Purchase timestamp
- `IsPaidOut`: Whether revenue has been paid to creator
- `PaidOutAt`: When payout was made

## API Endpoints

### 1. Create Creator Code

```http
POST /api/CreatorCode
Content-Type: application/json

{
  "code": "TECHNO",
  "creatorUserId": "user123",
  "discountPercent": 5,
  "revenueSharePercent": 5,
  "expiresAt": "2025-12-31T23:59:59Z",  // Optional
  "maxUses": 1000  // Optional
}
```

**Response:**
```json
{
  "id": 1,
  "code": "TECHNO",
  "creatorUserId": "user123",
  "discountPercent": 5,
  "revenueSharePercent": 5,
  "isActive": true,
  "createdAt": "2025-10-30T00:00:00Z",
  "updatedAt": "2025-10-30T00:00:00Z",
  "expiresAt": "2025-12-31T23:59:59Z",
  "maxUses": 1000,
  "timesUsed": 0
}
```

### 2. Get Creator Code

```http
GET /api/CreatorCode/{code}
```

### 3. Validate Creator Code

```http
GET /api/CreatorCode/validate/{code}
```

**Response:**
```json
{
  "isValid": true,
  "discountPercent": 5,
  "code": "TECHNO",
  "message": "Valid! Get 5% off"
}
```

### 4. Get Revenue Report

```http
GET /api/CreatorCode/{code}/revenue?startDate=2025-10-01&endDate=2025-10-31
```

**Response:**
```json
{
  "code": "TECHNO",
  "creatorUserId": "user123",
  "startDate": "2025-10-01T00:00:00Z",
  "endDate": "2025-10-31T23:59:59Z",
  "totalTransactions": 150,
  "revenueByCurrency": [
    {
      "currency": "USD",
      "totalRevenue": 1250.50,
      "totalTransactions": 100,
      "paidOut": 500.00,
      "unpaid": 750.50
    },
    {
      "currency": "EUR",
      "totalRevenue": 450.25,
      "totalTransactions": 50,
      "paidOut": 0,
      "unpaid": 450.25
    }
  ]
}
```

### 5. Update Creator Code

```http
PUT /api/CreatorCode/{code}
Content-Type: application/json

{
  "discountPercent": 10,  // Optional - update for special occasion
  "revenueSharePercent": 7,  // Optional
  "isActive": true,  // Optional
  "expiresAt": "2026-01-31T23:59:59Z",  // Optional
  "maxUses": 2000  // Optional
}
```

### 6. Get Creator Codes for User

```http
GET /api/CreatorCode/user/{userId}
```

## Usage in TopUp/Subscription Flow

When making a purchase, users can pass a creator code in the `TopUpOptions`:

```json
{
  "successUrl": "https://example.com/success",
  "userEmail": "user@example.com",
  "topUpAmount": 1000,
  "creatorCode": "TECHNO"
}
```

The system will:
1. Validate the creator code (check if active, not expired, under max uses)
2. Apply the discount to the checkout
3. Process the payment
4. Record revenue attribution in the `CreatorCodeRevenues` table
5. Increment the usage counter

## Revenue Calculation Example

**Original Price**: $10.00  
**Discount Percent**: 5%  
**Revenue Share Percent**: 5%  

**Calculations**:
- Discount Amount: $10.00 × 5% = $0.50
- Final Price: $10.00 - $0.50 = $9.50
- Creator Revenue: $9.50 × 5% = $0.475

## Special Occasions

To run special promotions, update the discount percentage:

```bash
# Increase discount to 10% for a limited time
PUT /api/CreatorCode/TECHNO
{
  "discountPercent": 10,
  "expiresAt": "2025-11-30T23:59:59Z"
}

# Revert back to 5% after promotion
PUT /api/CreatorCode/TECHNO
{
  "discountPercent": 5,
  "expiresAt": null
}
```

## Payout Workflow

1. Query unpaid revenue for a time period:
   ```http
   GET /api/CreatorCode/TECHNO/revenue?startDate=2025-10-01&endDate=2025-10-31
   ```

2. Calculate total unpaid amount per currency from the response

3. Process payout to creator (external to this system)

4. Mark revenue as paid out (requires additional endpoint or direct database update)

## Database Migration

To apply the creator code tables to your database:

```bash
dotnet ef database update --context PaymentContext
```

## Security Considerations

- Creator codes are case-insensitive (normalized to uppercase)
- Validation happens on both checkout creation and payment callback
- Failed creator code processing does not block the main transaction
- All revenue tracking is logged for audit purposes

## Future Enhancements

- Bulk payout marking endpoint
- Creator dashboard with analytics
- Referral tracking (who used whose code)
- Tiered revenue sharing based on performance
- Automatic code generation
- Admin panel for managing all creator codes
