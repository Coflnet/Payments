# Direct subscriptions (Stripe, PayPal) and discount codes

Lemon Squeezy is deprecated. New subscriptions (4, 13 or 52 week periods; Premium and Premium+; single and slot bundles such as
`l_premium-slots-4`) go to:

- direct Stripe and PayPal for `SUBSCRIPTIONS:DIRECT_COUNTRIES` (EU-27, GB, US by default), Coflnet handles the VAT (US: only up to
  the cap below);
- Stripe Managed Payments (Stripe is merchant of record) in the countries Stripe covers for tax (`SUBSCRIPTIONS:MANAGED_COUNTRIES`,
  see below). Countries covered by neither get Lemon Squeezy while it is enabled, otherwise no provider.

Switches: `SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED`, `SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED`, `SUBSCRIPTIONS:PAYPAL_ENABLED` (all default
false), `SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED` (default true, only used where no other provider applies).

## US direct cap

US buyers get direct Stripe/PayPal (no tax charged) only while fewer than `SUBSCRIPTIONS:US_DIRECT_LIMIT` (default 10) direct US
subscriptions are active. From the limit on, new US buyers are routed to Stripe Managed Payments (Stripe collects the US sales tax; `US` is
in `DefaultManagedCountries`, so it works with the defaults when `SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED` is on; with it off, a capped US buyer
gets Lemon Squeezy or nothing, never PayPal). Existing subscriptions are untouched; the cap only routes new checkouts.

- Active = a direct subscription (provider `stripe` without Managed Payments, or `paypal`) whose checkout country (`Subscriptions.CheckoutCountry`,
  written when the subscription is recorded: Stripe checkout metadata, PayPal `PaymentRequest.Country`) is US and that has not ended: status is
  not `expired`, and `cancelled` only counts while `EndsAt` lies in the future (cancelled without `EndsAt` counts as ended). `past_due`,
  `unpaid` and `paused` still count (they can recover). `UsDirectSubscriptionCap.ActiveDirect` is the single definition.
- Cheap: one `COUNT` on the `(CheckoutCountry, Status)` index, cached for 30 seconds (`UsDirectSubscriptionCap.CacheSeconds`) in the singleton routing.
  `GET /TopUp/subscribe/providers`, `discount/auto`, `stripe/subscribe`, `paypal/subscribe` and `lemonsqueezy/subscribe` refresh it and then ask
  `SubscriptionRouting`, so they always agree. Requests arriving while a count runs wait for it (one count at a time), so the
  requests right after a restart wait for the first count. Until a count succeeded the cap counts as reached (US buyers go to Managed
  Payments; a warning is logged once). A failing count keeps the previous routing (warning).
- A request takes one `SubscriptionRouting.Capture()` snapshot of the cap and makes all its routing decisions (mode, PayPal, providers)
  with it, so a refresh in between cannot give a US buyer a direct checkout without the IP-country check.
- A subscription counts once it is recorded (checkout completed / PayPal activated), not while its checkout is open, and the cache lags up to
  30 seconds, so concurrent checkouts around the limit can overshoot it by a few. That is accepted.
- The first time the count reaches the limit an info log "US direct subscription cap reached" is written (again only after the count fell below it).

## Where Managed Payments is offered

Stripe only takes over the indirect tax in the countries listed at https://docs.stripe.com/payments/managed-payments/tax-compliance
(cross-border sales of digital products). Elsewhere (e.g. AE, CL, PE, BR, AR, CO) the seller stays liable, so Managed Payments is not
offered there. `SUBSCRIPTIONS:MANAGED_COUNTRIES` (comma separated) defaults to that list as documented on 2026-11-17
(`SubscriptionRouting.DefaultManagedCountries`: the African, Asia-Pacific, European, EU, Latin American/Caribbean countries and CA, US
of the page; RS is listed there only for sellers not VAT registered in Serbia, which we are not). Re-check the page when extending it.

`SalesRestrictions.BlockedCountries` (TR, AE, SA, KR, VN, CL, MX, PE, MD) applies to every subscription provider, direct and managed,
so TR, SA, KR, VN, MX and MD are on Stripe's list but still not sold to. Removing a country from `BlockedCountries` is the only step
needed to offer Managed Payments there. The Managed Payments country is the buyer's selection (not matched against the IP), so the
IP country, when the lookup knows it, must be covered and not blocked as well; otherwise the Stripe checkout is refused. `GET /TopUp/subscribe/providers`, `discount/auto` and the checkout endpoints all read the same
routing, so an uncovered country gets Lemon Squeezy (if enabled) or no provider, never a discount offer or a Stripe/PayPal checkout.

## Pricing and VAT

Plan prices (e.g. `l_premium` 9.69 EUR per 4 weeks) are NET, as they were with Lemon Squeezy; the buyer pays the net price plus VAT.

- Order: plan price, creator code, then discount code (percent/fixed, with the period amount of the plan's billing period),
  rounded to cents = the net price.
- Direct Stripe and PayPal (Coflnet is merchant of record): gross cents = round-half-away-from-zero(net cents * (10000 + bp) / 10000),
  with `bp` the VAT rate of the buyer's verified (IP matched) country in basis points. 9.69 in DE (1900) = 1153 cents, GB (2000) = 1163,
  US = 969. `SubscriptionPricing.GrossCents` is the single implementation; checkout, PayPal first/recurring prices, plan switches,
  the end of a "first payment only" code and the renewal amount checks (`UserSubscription.PriceCents` = what the provider charges) all use it.
  Stripe gets the final amount (no `tax_behavior`, no automatic tax), PayPal the gross price override.
  Direct Stripe sessions send `adaptive_pricing[enabled]=false`, so direct buyers always pay EUR (Managed Payments rejects the parameter).
- Stripe receipts show the VAT: direct sessions attach an inclusive manual tax rate (display name "VAT", jurisdiction = the country, the
  configured rate; `StripeSubscriptionGateway.EnsureVatTaxRate` creates it once per country and rate, found again by its
  `coflnet_vat` metadata) to the gross price line, so receipts show rate and amount while the charged total stays the gross price.
  Not on Managed Payments sessions (not allowed there, Stripe taxes) and not for rate 0 (US).
- Rates come from `EXPERT_CONFIG:VAT_RATE_BASIS_POINTS:{ISO country}` (the same keys TransactionService uses for expert quotes; defaults in
  `appsettings.json`). The US defaults to 0 without a key (one-time Stripe/PayPal purchases do not collect US sales tax either). Any
  other direct country without a rate is refused (error log, `GET /TopUp/subscribe/providers` and `discount/auto` do not list the direct
  providers for it) instead of being undercharged.
- Stripe Managed Payments: the net price is sent with `tax_behavior` `exclusive` (`STRIPE:MANAGED_TAX_BEHAVIOR`, default `exclusive`) and
  Stripe adds and remits the tax. The amount is not verified for these (local currency), only the plan.
- Records: `PaymentRecord` has `GrossAmount` = paid, `TaxAmount` = VAT contained (direct: split off the gross with the country's rate;
  Managed Payments: Stripe's invoice tax), `Subtotal`/`NetAmount` = gross minus VAT. The payment event carries the gross; creator
  shares are based on the net price.
- The VAT country is fixed at checkout for the subscription's lifetime: the Stripe checkout metadata country, for PayPal the
  `PaymentRequest.Country` of its checkout (the user's remembered country changes with later purchases and is not used). Direct
  payments are recorded (and published) under that country, also when the billing address names another one. Stripe does not add
  tax itself, so the invoice shows no VAT line; the VAT is only in our records.
- Missing or invalid rates (whole basis points 0-10000; "25.5" is invalid, use 2550) are logged once at startup as an error listing
  the direct countries that are offered no direct provider.
- An open checkout is only reused for the same provider, prices, country and mode (direct or Managed Payments: a direct US
  checkout and a Managed Payments one have the same amount, so the mode is stored with the payment request).
- PayPal subscriptions take their creator and discount code from the checkout's `PaymentRequest` (Stripe from the metadata);
  creator shares are based on the net part of what a direct sale paid (Managed Payments: the agreed net EUR price).

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

Price order: creator code first, then the local code. `once` codes: on Stripe the session recurs at the full price and a one-time
coupon lowers the first invoice (below), PayPal prices the first cycle (TRIAL) differently from the following ones (REGULAR). Rounding is `Math.Round` (banker's), e.g.
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

Pricing on the session: the inline `price_data` unit amount is always the recurring price (direct: gross incl. VAT, managed: net).
Discount codes that apply to every renewal lower that amount. A "first payment only" code keeps the full recurring price and adds a
one-time Stripe coupon (`duration=once`) through the session's `discounts`, so Stripe's page reads "X today, then Y":

- direct: `amount_off` = full gross - discounted gross in EUR cents, coupon id `coflnet_once_{cents}_{currency}`;
- Managed Payments: `percent_off` (two decimals), coupon id `coflnet_once_pct_{percent*100}`; a percentage because Adaptive Pricing may
  charge in the buyer's local currency, where a fixed EUR amount would not apply.

Coupons are created on first use and then reused (`EnsureFirstPaymentCoupon`: get by id, create when missing, a concurrent create is
tolerated), so the number of coupons is bounded by the distinct discounts. A coupon found by id is only reused when its terms match
(duration `once`, valid, the same `amount_off` and currency or the same `percent_off`); otherwise an error is logged and the versioned id
`..._v2` (then `_v3`, up to `_v5`) is checked or created instead, so a coupon edited in the dashboard can never apply a wrong discount. The checkout metadata `priceCents` is the recurring price and
`firstPriceCents` the first invoice; the first invoice must charge exactly `firstPriceCents` (direct), every other one `priceCents`.
Nothing is repriced after the first invoice any more. Managed Payments does not list `discounts` or `custom_text` among its disallowed
session parameters; whether a fixed or percentage coupon is taxed on the discounted amount there (and that the coupon is applied together with
Adaptive Pricing) is not stated by Stripe and needs a live test checkout before the first sale.

Text next to the button (`StripeSubscriptionDisclosure`, one place for the legal wording): `custom_text.submit.message` states "Renews every
N weeks at X until cancelled. Cancel anytime in your account at sky.coflnet.com/account." plus, direct with VAT (not the US) "The price includes VAT.", managed "... before
taxes. Sold through Link (Stripe's reseller), which charges any applicable taxes." (Stripe's wording: Managed Payments receipts read "Sold through Link") `submit_type=pay` is allowed in
subscription mode by the Checkout Sessions API (also not on the Managed Payments disallowed list); it labels the button "Buy" instead of the
default "Subscribe". Both are tested against the request body Stripe.net sends.

### Place of supply evidence

Direct sales keep, per payment (`PaymentRecords`): `IpAddress` (the checkout request's IP), `CheckoutCountry` (the VAT country),
`BillingCountry` (Stripe: billing address of the checkout, PayPal: `subscriber.shipping_address.address.country_code`) and
`PaymentMethodCountry` (Stripe: issuing country of the card, `payment_intent.latest_charge.payment_method_details.card.country`).
The card is looked up with `IStripeSubscriptionGateway.GetCardCountry` (invoice -> paid payment intent -> latest charge): required for the
first invoice of a direct sale, best effort afterwards. When billing address and card agree on a country that differs from the checkout
country the first invoice is rejected: the subscription is cancelled at Stripe, the payment marked for a manual refund and an error logged.
One signal alone (card in a third country, no card country) does not reject. PayPal does not allow refusing after the fact: a payer country
that differs is recorded and logged as a warning only. PayPal provides almost no country evidence: the subscription details' `subscriber` holds
name, email, `payer_id` (no address), `shipping_address` (never returned, we create subscriptions with `shipping_preference=NO_SHIPPING`) and
`payment_source.card.billing_address` (only when the subscription is funded by a card, not by a PayPal wallet); the `PAYMENT.SALE.*` resource has no
payer fields. The payer country is therefore the card billing country when present, else null (stored in `BillingCountry`, exported as
`PayerCountry`). The Transaction Search API (`payer_info.country_code`) needs an extra permission and is deliberately not used; for wallet
payments the checkout (IP verified) country is the only evidence. Managed Payments is taxed by Stripe, so no card lookup there.

### Revenue export (VAT reporting)

`GET /RevenueExport/transactions/export?startDate=&endDate=[&provider=&country=&format=csv|json&includeAllStatuses=true]`: by default only
`Confirmed` payment records; with `includeAllStatuses=true` records of every status plus captured payments marked for a manual refund that have no
record. Numbers use a decimal point and `Timestamp` is `yyyy-MM-dd HH:mm:ss` UTC whatever the server culture; quoted fields double inner quotes. CSV columns: the original ones in their original order (including `Status`), then appended `CheckoutCountry`, `BillingCountry`,
`PaymentMethodCountry`, `PayerCountry`, `IpAddress`, `SubscriptionMode`. The JSON of `/RevenueExport/transactions` and of the export endpoint carries the
same fields (`/transactions` has never filtered by status).

- `Status` already was a column, so it is not appended a second time (a duplicate header would be ambiguous); it holds the mapping: `PaymentRecordStatus`
  Confirmed, Refunded, PartiallyRefunded, Disputed by name; a `PaymentRequest` left in `REFUND_PENDING` is `RefundPending`; `Rejected` is the mapping of
  `REJECTED` (`ExportStatus`). Rejected requests are not exported: a rejected PayPal checkout took no money itself, its sales are exported as
  `RefundPending`.
- Refund pending payments without a record are the `PaymentRequests` rows `MarkForManualRefund` writes (keyed by the provider's sale/invoice id, with the
  checkout country in `PaymentRequests.Country`; also the blocked-country one-time refunds of the callback controller). They appear with
  `TransactionId` `refund-pending-{request id}`, `ExternalReference` = the sale/invoice id, `Amount` as paid, `Currency` from the plan (direct sales are
  EUR), `Timestamp` = when marked (the sale time is not stored; payments are marked by their webhook, normally right after the sale;
  one-time checkouts marked before `UpdatedAt` was set there fall back to the checkout start; the date range filters by the same time), and are listed unless a PaymentRecord exists for that provider and id. A sale of a subscription we did not sell
  has no country (empty `CheckoutCountry`, `UserCountry` UNKNOWN).
- `SubscriptionMode`: `direct` (Stripe/PayPal subscription payments Coflnet remits VAT for), `managed` (Stripe Managed Payments), the provider for subscription
  payments of other merchants of record (`lemonsqueezy`, `googlepay`), empty for
  everything that is not a subscription payment. `PayerCountry` is PayPal only (see above); `BillingCountry` is Stripe's billing address only.

### Refunds and creator shares

A full or partial refund reverses the creator share of the payment: `CreatorCodeService.ReverseRevenueAsync` adds a negative
`CreatorCodeRevenues` row (`{payment id}#refund:{key}`) next to the original, proportional to the refunded part and capped at the share. Stripe
uses the cumulative refund of the charge (key `charge`), PayPal the refund id (`full` for refunded/reversed sales; a negative refund amount
counts by its magnitude). Rows are never changed: when the refund of a key grows, the increase is a new row (`{key}+1`, `+2`, ...) dated
then, so a reversal already paid out or reported stays as it was. Redeliveries change nothing, also concurrent ones: reversal rows are unique
(`UX_CreatorCodeRevenues_Reversal`) and a unique violation is treated as "already done"; a share already paid out becomes a negative
unpaid balance. Revenue reports net the rows out and do not
count them as transactions. Lemon Squeezy subscriptions never book creator revenue, so there is nothing to reverse there.

### Withdrawal declaration

`TopUpOptions.servicePerformanceDeclaration` (`SubscriptionDeclaration`, same evidence as the declaration of purchases with coins) is accepted by
`stripe/subscribe` and `paypal/subscribe`, validated like the prepaid one (same hashes), stored as `ServicePerformanceDeclaration` with
`PaymentRequestId` and refused for another user's or plan's declaration id. A retry with the same id moves it to the new checkout, unless
its checkout was paid (or it belongs to a prepaid purchase): then it is refused, so a subscription keeps its declaration. Like the prepaid flow it is only mandatory with
`LEGAL:ENFORCE_SERVICE_PERFORMANCE_DECLARATION=true` (error `service_performance_declaration_required`); otherwise a missing one is logged.

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
`DiscountCodes` (with `PeriodAmounts` as a JSON text column and `AutoApply`), `PayPalPlans` and `PaymentRequests.RecurringAmount`, plus the
evidence columns `PaymentRecords.CheckoutCountry/BillingCountry/PaymentMethodCountry` and `ServicePerformanceDeclarations.PaymentRequestId`,
`Subscriptions.CheckoutCountry` with its index `(CheckoutCountry, Status)`, and the partial unique index `UX_CreatorCodeRevenues_Reversal` on
`CreatorCodeRevenues.TransactionReference` for reversal rows only (`LIKE '%#refund:%'`): sale rows written by earlier releases were never checked for
duplicates, so a full unique index could fail on them and they are never deleted; the script stops if reversal rows are already duplicated
(existing tables, so no new grants; `Migrations/apply-direct-subscriptions.sh` runs the same file per instance).
