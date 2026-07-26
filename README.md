# Payments
Handles payments, and access to digital goods and services

## Configuration
Configuration is handled via [asp.net configuration providers](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-6.0#environment-variables)
Keys with defaults are set in [`appsettings.json`](appsettings.json)

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
The Stripe API key (`STRIPE__KEY`) is used for Checkout Sessions and pre-capture country validation:

| Operation | API Call | Permission |
|-----------|----------|:----------:|
| Create checkout session with dynamic payment methods | `SessionService.CreateAsync()` | Write |
| List sessions by PaymentIntent | `SessionService.ListAsync()` | Read |
| Expire stale sessions | `SessionService.ExpireAsync()` | Write |
| Read provider country and capture/cancel supported authorizations | `PaymentIntentService` | Read & Write |
| Read the expanded payment method country | PaymentIntent `payment_method` expansion | Read |

Webhook verification uses the signing secret. Card and Link use per-method manual capture without
disabling other methods. When Stripe supplies a payment-method country it is authoritative; otherwise
the country or locale supplied at session creation must match the independently looked-up IP country.
Disallowed automatically captured payments are marked for manual refund and are not credited.

> **Restricted key:** Enable only **Checkout Sessions → Read & Write**, **Payment Intents → Read & Write**, and **Payment Methods → Read**.

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

No PayPal Payouts, Disputes, Subscriptions, Invoicing, Vault, or Transaction Search APIs are used.

> **Live app features:** In the PayPal Developer Dashboard, enable **only** the "Accept payments" feature for the live REST API app. Disable Payouts, Customer disputes, Transaction search, Invoicing, Subscriptions, Partner referrals, Vault, and Webhooks management.



## Events 
This microservice can produce transaction events into a Kafka Topic.
To configure it set the configuration variables `KAFKA_HOST` and `KAFKA__TRANSACTION_TOPIC__NAME`.
The format and fields of the events can be seen in the [TransactionEvent class](Models/TransactionEvent.cs) 
