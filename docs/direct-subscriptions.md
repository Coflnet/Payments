# Direct subscriptions (Stripe, PayPal) and discount codes

Lemon Squeezy is deprecated. New subscriptions (4, 13 or 52 week periods; Premium and Premium+; single and slot bundles such as
`l_premium-slots-4`) go to:

- direct Stripe and PayPal for `SUBSCRIPTIONS:DIRECT_COUNTRIES` (EU-27, GB, US by default), Coflnet handles the VAT;
- Stripe Managed Payments (Stripe is merchant of record) everywhere else.

Switches: `SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED`, `SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED`, `SUBSCRIPTIONS:PAYPAL_ENABLED` (all default
false), `SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED` (default true, only used where no other provider applies).

## Endpoints

| Route | Purpose |
| --- | --- |
| `GET /TopUp/subscribe/providers?country=DE&productSlug=l_premium` | Providers a buyer can choose |
| `POST /TopUp/stripe/subscribe?userId=&productId=` | Stripe checkout (direct or Managed Payments by country) |
| `POST /TopUp/paypal/subscribe?userId=&productId=` | PayPal subscription, redirect to the returned `directLink` |
| `GET /TopUp/discount/validate?code=&isSubscription=true&provider=stripe` | Validates a code; returns `amount` and `periodAmounts` |
| `GET /TopUp/discount/auto?country=DE` | The promotion to offer (see below), 204 when none |
| `GET/POST /api/DiscountCode` | List / create-or-replace codes (internal) |

Slot bundles use the same endpoints and accept discount and creator codes like single subscriptions. The seats are granted
per paid period and bound to the provider subscription id.

## Discount codes

Fields of `POST /api/DiscountCode`: `code` (stored upper case), `name`, `amountType` (`percent` or `fixed` = cents), `amount`,
`periodAmounts` (billing weeks 4/13/52 to amount; a period with an entry overrides `amount`, other periods use `amount`),
`autoApply`, `subscriptionOnly`, `duration` (`forever` = every payment, `once` = first payment only), `maxRedemptions`,
`startsAt`, `expiresAt`, `isActive`. `autoApply` requires `subscriptionOnly`.

Price order: creator code first, then the local code. `once` codes: Stripe changes the price back after the first invoice,
PayPal prices the first cycle (TRIAL) differently from the following ones (REGULAR). Rounding is `Math.Round` (banker's), e.g.
50% of 29.69 is 14.84.

Black Friday 2026 (25% for 4 weeks, 50% for 13 and 52 weeks), applied automatically. Payments is internal, so port-forward first:

```bash
kubectl --context <ctx> -n <payments-namespace> port-forward svc/payments 5020:80 &
curl -X POST http://localhost:5020/api/DiscountCode -H 'Content-Type: application/json' -d '{
  "code": "BLACKFRIDAY26",
  "name": "Black Friday",
  "amountType": "percent",
  "amount": 25,
  "periodAmounts": { "4": 25, "13": 50, "52": 50 },
  "duration": "forever",
  "subscriptionOnly": true,
  "autoApply": true,
  "isActive": true,
  "startsAt": "2026-11-27T00:00:00Z",
  "expiresAt": "2026-11-30T23:59:59Z"
}'
```

Clients call `GET /TopUp/discount/auto?country=<buyer country>`, show the per-period prices and send `code` as `discountCode`
with the checkout; it goes through the normal validation (and counts as one redemption). When several codes are active the newest
wins. The response lists `providers` that honor it. Local codes are NOT honored by Lemon Squeezy checkouts: a code sent to
`lemonsqueezy/subscribe` is validated against Lemon Squeezy only and rejected ("Invalid or expired"), so a buyer routed there is
never charged a price different from the one shown. `discount/auto` returns 204 for a country where only Lemon Squeezy applies.

Codes are accepted for new checkouts until `expiresAt` (the website stops showing them then), but a checkout started before
`expiresAt` stays payable at the discounted price for a 24 hour grace: Stripe sessions keep Stripe's default lifetime of 24
hours, and a PayPal subscription whose first payment is more than 24 hours after the code expired is cancelled at PayPal and
not granted (its payments are marked for a manual refund). An open checkout is only reused for the same first and renewal price.

## Stripe

Activate Managed Payments (needed for every country outside the direct list): https://dashboard.stripe.com/settings/managed-payments

Webhook endpoint `/callback/stripe` (secret `STRIPE:SIGNING_SECRET`), events: `checkout.session.completed`,
`checkout.session.async_payment_succeeded`, `invoice.paid`, `invoice.payment_failed`, `customer.subscription.created`,
`customer.subscription.updated`, `customer.subscription.deleted`, `charge.refunded` (plus the existing one-time events).

Discounts are applied through the inline `price_data` unit amount, not Stripe coupons, for direct and Managed Payments sessions.
Stripe's Managed Payments docs do not say whether `discounts` are allowed together with `managed_payments[enabled]=true`
(we could not confirm it), so coupons are deliberately not used; verify with a live test checkout before ever changing that.

## PayPal

Webhook endpoint `/callback/paypal` (id in `PAYPAL:WEBHOOK_ID`), events `BILLING.SUBSCRIPTION.*`, `PAYMENT.SALE.COMPLETED`,
`PAYMENT.SALE.REFUNDED`, `PAYMENT.SALE.REVERSED`. The REST app needs the Subscriptions feature enabled.

PayPal plans are created automatically on the first checkout of a (product slug, billing weeks, currency): a catalog product
(`SERVICE`, `SOFTWARE`) and a billing plan (`WEEK` interval, 4/13/52) are created and the plan id is stored in the `PayPalPlans`
table (unique on slug+weeks+currency; a concurrent first checkout keeps the stored plan, an orphan plan at PayPal is harmless).
Each plan is a one cycle TRIAL (sequence 1) followed by an infinite REGULAR cycle (sequence 2) at the list price; every
subscription overrides the price of both cycles, which is how `once` codes work. `PAYPAL:SUBSCRIPTION_PLANS:{slug}` is still
read as an optional override (that plan must have the same TRIAL + REGULAR structure) and is no longer required.
Each sale is granted only when it matches the agreed price: the first sale the first-cycle price, later sales the recurring price.

## Database

`Migrations/20261117120348_DirectSubscriptions` (EF) and the hand-written `.cockroach.sql` beside it add the subscription columns,
`DiscountCodes` (with `PeriodAmounts` as a JSON text column and `AutoApply`), `PayPalPlans` and `PaymentRequests.RecurringAmount`.
