# Payments
Handles payments, and access to digital goods and services

## Configuration
Configuration is handled via [asp.net configuration providers](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-6.0#environment-variables)
Keys with defaults are set in [`appsettings.json`](appsettings.json)

### Active premium plus owners

The existing `/metrics` endpoint exports `payments_active_product_owners{product="premium_plus"}`
once per minute. It preserves `GET /Products/service/premium_plus/count`: count distinct
`OwnerShips.UserId` groups whose ownership has not expired and whose product slug or product
group slug matches `premium_plus`. Multiple matching ownerships count once. The legacy nullable
user group also counts once. Tier slots alone do not count; this is the existing ownership cohort.

`payments_active_product_owners_last_success_timestamp_seconds{product="premium_plus"}`
records a successful computation. Neither metric publishes an initial zero, and database errors
leave the previous count and timestamp unchanged. Consumers must filter stale computations,
even while Prometheus continues scraping successfully. For a Prometheus target identified by
`job` and `instance`, the equivalent fresh average is:

```promql
avg(
  payments_active_product_owners{product="premium_plus"}
  and on(job, instance, product)
  (time() - payments_active_product_owners_last_success_timestamp_seconds{product="premium_plus"} < 180)
)
```

These aggregate metrics use the existing scrape endpoint and contain no user identifiers.

### How purchases work
1. Start top up 
2. wait till payment is verified
3. (optional) plan purchase (locks some amount of balance)
4. purchase product/service 

## Setup
Becuase it is the esiest by default stripe is used. 
To configure stripe get your stripe `KEY` and `SIGNING_SECRET` from stripe.com and set them as configuration. 
(either modify appsettings.json or set the enviromentvariables `STRIPE__KEY` and `STRIPE__SIGNING_SECRET`)  
Next create a webhook callback to `/Callback/stripe` that triggers on confirmed purchase.

### Stripe — Minimal API Key Permissions
The restricted API key (`STRIPE__KEY`) needs these permissions for checkout, country validation, and subscription billing.
Stripe's Write permission includes Read. [Stripe restricted API keys](https://docs.stripe.com/keys/restricted-api-keys#assign-permissions-to-a-restricted-api-key)

| Stripe resource | Permission | Operation | API call |
|-----------------|:----------:|-----------|----------|
| Checkout Sessions | Write | Create, read, list, and expire checkout sessions | `SessionService` |
| Payment Intents | Write | Read provider country and capture or cancel supported authorizations | `PaymentIntentService` |
| Charges | Read | Read the card country of a subscription payment | PaymentIntent `latest_charge` expansion |
| Payment Methods | Read | Read the expanded payment method country | PaymentIntent `payment_method` expansion |
| Customers | Write | Create and tag customers for Managed Payments subscriptions | `CustomerService.CreateAsync()`, `CustomerService.UpdateAsync()` |
| Coupons | Write | Look up and create first-payment coupons | `CouponService` |
| Tax Rates | Write | Look up and create inclusive VAT rates | `TaxRateService` |
| Products and Prices | Write | Look up and create plan products and prices | `ProductService`, `PriceService` |
| Subscriptions | Write | Read, update, and cancel subscriptions | `SubscriptionService` |
| Subscription Schedules | Write | Read, create, update, and release renewal schedules | `SubscriptionScheduleService` |
| Invoices and Invoice Payments | Read | Read subscription invoices and their payments | `InvoiceService`, `InvoicePaymentService` |
| Disputes | Read | Look up dispute status | `DisputeService.GetAsync()` |

Webhook verification uses the signing secret. Card and Link use per-method manual capture without
disabling other methods. When Stripe supplies a payment-method country it is authoritative; otherwise
the country or locale supplied at session creation must match the independently looked-up IP country.
Disallowed automatically captured payments are marked for manual refund and are not credited.

The restricted key intentionally omits Refunds permission. The subscription invoice refund endpoint calls
`RefundService.CreateAsync()` and requires a key with that permission to issue a refund.

### CoinGate country verification
CoinGate requests must set `TopUpOptions.Country` from an explicit user selection and
`TopUpOptions.UserIp` from the trusted ingress. The service looks up the IP country using
`IP_COUNTRY__BASE_URL` (default: `https://ipapi.co/`) and falls back to
`IP_COUNTRY__FALLBACK_BASE_URL` (default: `https://api.country.is/`). It creates the order only
when both ISO country codes match and the country is eligible.

#### Paypal
Paypal can be configured with `PAYPAL__SECRET`, `PAYPAL__ID` and `PAYPAL__IS_SANDBOX` 
Create a webhook callback to `/Callback/paypal` to allow for payments to be verified.

### PayPal — Minimal API Permissions
PayPal uses the **Orders API v2** (`PayPalCheckoutSdk`). The REST API app credentials (`PAYPAL__ID` / `PAYPAL__SECRET`) are used for three operations:

| Operation | API Call | Permission |
|-----------|----------|:----------:|
| Create order | `OrdersCreateRequest` | Accept payments |
| Capture order (complete payment) | `OrdersCaptureRequest` | Accept payments |
| Get order details | `OrdersGetRequest` | Accept payments |

The code handles these webhook events (no API key needed — events carry their own data):
- `CHECKOUT.ORDER.APPROVED` → triggers capture
- `PAYMENT.CAPTURE.COMPLETED` → updates transaction reference
- `PAYMENT.CAPTURE.REFUNDED` → reverts purchase locally

Direct subscriptions (see [docs/direct-subscriptions.md](docs/direct-subscriptions.md)) additionally use the **Subscriptions API**:
create catalog products and billing plans (`/v1/catalogs/products`, `/v1/billing/plans`, created automatically on the first
checkout of a plan), create/get/cancel subscriptions and list their transactions (`/v1/billing/subscriptions`), and get/refund
subscription sales (`/v1/payments/sale`). Webhook events: `BILLING.SUBSCRIPTION.*`, `PAYMENT.SALE.COMPLETED`,
`PAYMENT.SALE.REFUNDED`, `PAYMENT.SALE.REVERSED`.

No PayPal Payouts, Disputes, Invoicing, Vault, or Transaction Search APIs are used.

> **Live app features:** In the PayPal Developer Dashboard, enable **only** the "Accept payments" and "Subscriptions" features for the live REST API app. Disable Payouts, Customer disputes, Transaction search, Invoicing, Partner referrals, Vault, and Webhooks management.



## Events 
This microservice can produce transaction events into a Kafka Topic.
To configure it set the configuration variables `KAFKA_HOST` and `KAFKA__TRANSACTION_TOPIC__NAME`.
The format and fields of the events can be seen in the [TransactionEvent class](Models/TransactionEvent.cs) 
