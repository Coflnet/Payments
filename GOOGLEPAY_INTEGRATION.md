# Google Play Billing integration

Payments verifies one-time CoflCoins purchases through the Android Publisher API. The [Google Play subscriptions runbook](docs/google-play-subscriptions.md) covers Premium and Premium+, the subscription endpoints, the hand-applied migration, and authenticated Pub/Sub setup.

## CoflCoins

`POST /api/googlepay/verify` accepts the Play product ID, purchase token, package, and Payments user ID.

```json
{
  "productId": "coins_100",
  "purchaseToken": "purchase_token_from_google_play",
  "packageName": "com.coflnet.sky",
  "userId": "payments_user_id"
}
```

Payments looks up the SKU in its product catalog under provider `googlepay`, verifies the purchase state, credits the order, and acknowledges it. If a response is lost after credit and acknowledgment, retry the same request. Payments returns `isValid: true` when the acknowledged order has an existing matching credit.

`POST /api/googlepay/product-pricing` returns the Play catalog pricing and listings for a one-time SKU. The legacy subscription verification and pricing routes were retired. Clients use subscription eligibility and completion, and obtain localized subscription prices from Play Billing.

## Real-time developer notifications

Cloud Pub/Sub pushes to `POST /callback/googlepay`. Both `GOOGLEPAY:PUBSUB_AUDIENCE` and `GOOGLEPAY:PUBSUB_SERVICE_ACCOUNT` are required. Missing either setting or an invalid issuer, audience, account email, or `email_verified` claim returns 401. The app's `/api/googlepay/verify` top-up flow continues independently. [Authenticated push setup](docs/google-play-subscriptions.md#pubsub-and-rtdn)

Subscribe to one-time as well as subscription notifications. Payments reverses credited coins for a full void refund (`refundType=1`) only after `purchases.voidedpurchases.list` with `type=1` confirms both the order ID and purchase token within the last 30 days. The same confirmation and retry rules apply to one-time and subscription voids. Keep purchase tokens out of application logs and support messages. [Void confirmation and retry rules](docs/google-play-subscriptions.md#backend-flow)

Configure the Android Publisher API credentials through `GOOGLEPAY:ServiceAccountKeyFile`, the app through `GOOGLEPAY:PackageName`, and the API client name through `GOOGLEPAY:ApplicationName`.
