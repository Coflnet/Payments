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
The Stripe API key (`STRIPE__KEY`) is used **only** for Checkout Session operations:

| Operation | API Call | Permission |
|-----------|----------|:----------:|
| Create checkout session | `SessionService.CreateAsync()` | Write |
| List sessions by PaymentIntent | `SessionService.ListAsync()` | Read |
| Expire stale sessions | `SessionService.ExpireAsync()` | Write |

**No other Stripe resources are accessed.** Webhook verification uses the signing secret, not the API key. Payment intent, charge, and refund details are all read from webhook payloads — no Stripe API calls are made for them.

> **Restricted key:** Create a restricted key in the Stripe Dashboard with only **Checkout Sessions → Read & Write**.

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
