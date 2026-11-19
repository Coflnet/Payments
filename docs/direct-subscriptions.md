# Direct subscriptions (Stripe, PayPal) and discount codes

Lemon Squeezy is deprecated. New subscriptions (4, 13 or 52 week periods; Premium and Premium+; single and slot bundles such as
`l_premium-slots-4`) go to:

- direct Stripe and PayPal for `SUBSCRIPTIONS:DIRECT_COUNTRIES` (EU-27, GB, US by default), Coflnet handles the VAT (US: only up to
  the cap below, per state);
- Stripe Managed Payments (Stripe is merchant of record) in the countries Stripe covers for tax (`SUBSCRIPTIONS:MANAGED_COUNTRIES`,
  see below). Countries covered by neither get Lemon Squeezy while it is enabled, otherwise no provider.

Switches: `SUBSCRIPTIONS:STRIPE_DIRECT_ENABLED`, `SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED`, `SUBSCRIPTIONS:PAYPAL_ENABLED` (all default
false), `SUBSCRIPTIONS:LEMONSQUEEZY_CHECKOUT_ENABLED` (default true, only used where no other provider applies).

## US direct cap (per state)

US buyers get direct Stripe/PayPal (no tax charged) only while fewer than `SUBSCRIPTIONS:US_DIRECT_LIMIT` (default 10) direct subscriptions
are active **for the buyer's state**. From the limit on, new buyers of that state are routed to Stripe Managed Payments (Stripe collects the US
sales tax; `US` is in `DefaultManagedCountries`, so it works with the defaults when `SUBSCRIPTIONS:STRIPE_MANAGED_ENABLED` is on; with it off, a
capped US buyer gets Lemon Squeezy or nothing, never PayPal). Other states are unaffected (10 in CA cap CA, not NY). Existing subscriptions are
untouched; the cap only routes new checkouts.

- The state is the optional `region` of the request: `TopUpOptions.region` on the checkout endpoints, `region=` on `GET /TopUp/subscribe/providers` and
  `GET /TopUp/discount/auto`. It is the ISO 3166-2 subdivision code without the country prefix (`CA`, `NY`, `DC`), upper cased and trimmed
  (1-3 letters/digits, anything else counts as missing). SkyApi fills it from Cloudflare's `cf-region-code` header, never from the browser.
  **Unknown or missing region for a US buyer = cap reached** (Managed Payments), the conservative choice. Countries other than the US ignore it.
- The checkout state is stored: `Subscriptions.CheckoutRegion` (Stripe: checkout metadata `region`, PayPal: `PaymentRequest.CheckoutRegion`),
  `PaymentRequests.CheckoutRegion` and `PaymentRecords.CheckoutRegion` (evidence; also the `CheckoutRegion` export column). For direct Stripe the
  state of the billing address entered at Checkout (invoice `customer_address.state`) is recorded as `PaymentRecords.BillingRegion`; a mismatch with
  the checkout state never rejects a payment.
- Active = a direct subscription (provider `stripe` without Managed Payments, or `paypal`) whose checkout country (`Subscriptions.CheckoutCountry`,
  written when the subscription is recorded: Stripe checkout metadata, PayPal `PaymentRequest.Country`) is US and that has not ended: status is
  not `expired`, and `cancelled` only counts while `EndsAt` lies in the future (cancelled without `EndsAt` counts as ended). `past_due`,
  `unpaid` and `paused` still count (they can recover). `UsDirectSubscriptionCap.ActiveDirect` is the single definition; the count is grouped by
  `CheckoutRegion`.
- Active US direct subscriptions without a region (none exist in production yet, so this is only a definition for completeness) are counted under the
  "unknown" bucket (`UsDirectSubscriptionCap.UnknownRegion`), which belongs to no state and so counts toward none.
- Cheap: one grouped `COUNT` on the `(CheckoutCountry, CheckoutRegion, Status)` index returns the counts of all states, cached for 30 seconds
  (`UsDirectSubscriptionCap.CacheSeconds`) in the singleton routing.
  `GET /TopUp/subscribe/providers`, `discount/auto`, `stripe/subscribe`, `paypal/subscribe` and `lemonsqueezy/subscribe` refresh it and then ask
  `SubscriptionRouting`, so they always agree. Requests arriving while a count runs wait for it (one count at a time), so the
  requests right after a restart wait for the first count. Until a count succeeded the cap counts as reached for every state (US buyers go to Managed
  Payments; a warning is logged once). A failing count keeps the previous counts (warning).
- A request takes one `SubscriptionRouting.Capture(region)` snapshot of the cap of its state and makes all its routing decisions (mode, PayPal,
  providers) with it, so a refresh in between cannot give a US buyer a direct checkout without the IP-country check.
- A subscription counts once it is recorded (checkout completed / PayPal activated), not while its checkout is open, and the cache lags up to
  30 seconds, so concurrent checkouts around the limit can overshoot it by a few. That is accepted.
- The first time the count of a state reaches the limit an info log "US direct subscription cap reached in {Region}" is written (again only after that
  state's count fell below it).

## Where Managed Payments is offered

Stripe only takes over the indirect tax in the countries listed at https://docs.stripe.com/payments/managed-payments/tax-compliance
(cross-border sales of digital products). Elsewhere (e.g. AE, CL, PE, BR, AR, CO) the seller stays liable, so Managed Payments is not
offered there. `SUBSCRIPTIONS:MANAGED_COUNTRIES` (comma separated) defaults to that list as documented on 2026-11-17
(`SubscriptionRouting.DefaultManagedCountries`: the African, Asia-Pacific, European, EU, Latin American/Caribbean countries and CA, US
of the page; RS is listed there only for sellers not VAT registered in Serbia, which we are not). Re-check the page when extending it.

Why each restriction list exists, and where it applies:

| List | Reason | Direct Stripe/PayPal subscriptions | One-time Stripe/PayPal/CoinGate | Managed Payments |
| --- | --- | --- | --- | --- |
| `SalesRestrictions.BlockedCountries` (TR, AE, SA, KR, VN, CL, MX, PE, MD) | tax only: Coflnet would owe local tax as a foreign seller | blocked | blocked | NOT applied (Stripe is merchant of record and pays the tax) |
| `SALES:DIRECT_PROVIDER_BLOCKED_COUNTRIES` (default CH, LI) and Northern Ireland postcodes (GB `BT`) | Swiss VAT / Northern Ireland registration, where Coflnet is the seller | blocked | blocked | not applied (Stripe handles it; CH/LI are on Stripe's list) |
| `SUBSCRIPTIONS:MANAGED_COUNTRIES` | where Stripe takes over the tax (list below) | n/a | n/a | required (AE, CL, PE are not on it) |
| `SUBSCRIPTIONS:MANAGED_BLOCKED_COUNTRIES` (default `BY`) | non-tax reasons (sanctions) that apply whoever the seller is | n/a | n/a | blocked |

So TR, SA, KR, VN, MX and MD get Managed Payments subscriptions (they are on Stripe's list) while staying refused for direct subscriptions and
one-time purchases; AE, CL and PE get nothing from Stripe. `MANAGED_BLOCKED_COUNTRIES` defaults to `BY` (owner decision 2026-11-18: Stripe does not support Belarus and the cards of the
larger Belarusian banks no longer work at EU shops; no EU ban on consumer sales was found); setting it replaces the default. Put sanctioned
countries there (the blocked list wins over `MANAGED_COUNTRIES`). The Managed Payments country is the buyer's selection (not matched against the IP), so the
IP country, when the lookup knows it, must be covered by Stripe and not managed-blocked as well; otherwise the Stripe checkout is refused (a buyer located in AE can not pick TR to get Managed Payments; a buyer
located in TR can pick any covered country for Managed Payments, but never a direct one, where selected and IP country must match). `GET /TopUp/subscribe/providers`, `discount/auto` and the checkout endpoints all read the same
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
- 13 and 52 week plans continue every 4 weeks after the first term (see "4-week renewal" below). The renewal price is the
  4-week list price of the same tier, creator code applied, then a `forever` code with its 4-week amount (`periodAmounts["4"]`,
  else `amount`); a `once` code does not apply. Direct: plus the VAT of the same checkout country; Managed Payments: net
  (`SubscriptionPricing.RenewalNet`). `PaymentRequests.RecurringAmount` holds what every later payment charges (for 13/52 weeks
  the 4-week renewal price), so an open checkout is only reused for the same renewal price too.

## Endpoints

| Route | Purpose |
| --- | --- |
| `GET /TopUp/subscribe/providers?country=DE&productSlug=l_premium&region=CA` | Providers a buyer can choose (`region`: US state, see the cap) |
| `POST /TopUp/stripe/subscribe?userId=&productId=` | Stripe checkout (direct or Managed Payments by country) |
| `POST /TopUp/paypal/subscribe?userId=&productId=` | PayPal subscription, redirect to the returned `directLink` |
| `GET /TopUp/discount/validate?code=&isSubscription=true&provider=stripe` | Validates a code; returns `amount` and `periodAmounts` |
| `GET /TopUp/discount/auto?country=DE&region=CA` | The promotion to offer (see below), 204 when none |
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
13/52 week checkouts also carry `renewalProductSlug` and `renewalPriceCents` (the 4-week renewal, see below); their `priceCents`
is the line item's 13/52 week price, which is never charged again. Managed Payments does not list `discounts` or `custom_text` among its disallowed
session parameters; whether a fixed or percentage coupon is taxed on the discounted amount there (and that the coupon is applied together with
Adaptive Pricing) is not stated by Stripe and needs a live test checkout before the first sale.

Text next to the button (`StripeSubscriptionDisclosure`, one place for the legal wording): `custom_text.submit.message` states "Renews every
4 weeks at X until cancelled. Cancel anytime in your account at sky.coflnet.com/account." (13/52 week plans: "After the first N weeks it
continues every 4 weeks at Y until cancelled. Cancel anytime ...", Y = the renewal price, well below `MaxLength` 1200) plus, direct with VAT (not the US) "The price includes VAT.", managed "... before
taxes. Sold through Link (Stripe's reseller), which charges any applicable taxes." (Stripe's wording: Managed Payments receipts read "Sold through Link") No `submit_type` is sent:
Stripe refuses `submit_type=pay` in subscription mode (seen live 2026-11-19), so the button keeps Stripe's "Subscribe" label. The request
body Stripe.net sends is tested to contain no `submit_type`.

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
`PaymentMethodCountry`, `PayerCountry`, `IpAddress`, `SubscriptionMode`, `CheckoutRegion` (the buyer's state at checkout). The JSON of `/RevenueExport/transactions` and of the export endpoint carries the
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

## 4-week renewal of 13 and 52 week plans

§ 309 Nr. 9 b BGB (consumer contracts since March 2022): an automatic renewal in standard terms may only continue for an indefinite
term that can be cancelled with at most one month's notice. Owner decision: after the first 13 or 52 weeks a subscription continues
as the 4-week plan of the same tier at that plan's 4-week price until cancelled, for every provider (direct Stripe, Managed Payments,
PayPal), every country and slot bundles alike.

- The renewal plan (`SubscriptionRenewal`, `SubscriptionRenewalPlans`): a sellable 4-week subscription plan (not disabled, price > 0)
  with the same tier (`TierRank`: tier groups / `SlotTier`), slot count and currency. Several matches: the one whose slug the long
  plan's slug starts with (`l_premium` for `l_premium-year`), otherwise none. `SUBSCRIPTIONS:RENEWAL_PLAN:{long slug}` = 4-week slug
  overrides the choice (only a valid renewal plan is accepted). A 13/52 week plan without a renewal plan is not sold (checkout error,
  the discount code is not redeemed).
- Local state: `Subscriptions.RenewalProductId` (the 4-week plan), `RenewalPriceCents` (what each renewal charges, gross for direct,
  net for Managed Payments) and, Stripe only, `RenewalScheduleId`. The subscription keeps its 13/52 week `Product` until the first
  renewal is paid; that payment converts it (`Product` = the renewal plan, `PriceCents` = `RenewalPriceCents`) and grants 4 weeks
  (the 4-week plan's ownership). From then on it is a normal 4-week subscription (plan switches, refunds, ...).
- Stripe (direct and Managed Payments): Checkout can not create a schedule, so once the first term is granted (`invoice.paid`), and on
  every `checkout.session.completed` / `customer.subscription.*` event after that, the subscription gets a subscription schedule
  (`StripeRenewalSchedule`, `from_subscription`, then one update): phase 0 is the current phase as Stripe derived it, ending at the
  current period end; phase 1 bills `price_data` on the plan product `coflnet_plan_{renewal slug}` (so `StripePlans.SlugOf` names the
  renewal plan once it is billed) every 4 weeks at `RenewalPriceCents`, `billing_cycle_anchor=phase_start`, `proration_behavior=none`,
  4 weeks long, phase metadata `priceCents` = the renewal price; `end_behavior=release` (after that period the subscription keeps
  billing every 4 weeks without a schedule). Direct sales get the inclusive VAT rate on the renewal item as on the checkout, Managed
  Payments the snapshot's `tax_behavior` (exclusive) and no tax rate. Schedule metadata `coflnet_renewal` = `{slug}:{cents}` marks
  ours; a schedule someone else made with planned changes is never overwritten (error logged). Lists are only sent when not empty
  (Stripe.net sends a list that was set to null as an empty value, which would unset it); a kept phase discount is sent as exactly
  one of `discount` (preferred), `promotion_code`, `coupon`. A schedule created by `from_subscription` is released again when the
  update fails, so no schedule with only the current phase stays attached.
  Swapping the price directly is not an option: a price with another interval moves the billing date to the change and charges at once
  (docs.stripe.com/billing/subscriptions/change-price, "Billing periods"); Stripe recommends schedules for changes at period end.
  Managed Payments supports subscription schedules since February 2026 (docs.stripe.com/payments/managed-payments/changelog).
- The first 4-week invoice is granted whether its billing reason is `subscription_cycle` or `subscription_update` (Stripe does not
  document which one a phase change with a new interval produces): an `updated` invoice while the subscription is in its first term
  and Stripe bills its renewal plan is that renewal, never a proration. Direct: it must charge exactly `RenewalPriceCents`.
- Cancel at period end: Stripe refuses `cancel_at_period_end` on a subscription a schedule manages, so the renewal schedule is released
  first: ours, and for 13/52 week plans any schedule without changes planned by someone else (also one whose id was never stored).
  Resume schedules it again; when that fails the resume is undone (schedule released, cancelled at period end again), so a resumed year
  never renews for another year; if even that fails a critical error is logged. A plan switch during the first term releases the schedule, swaps the price, reprices the renewal with the 4-week plan
  of the new tier (`RecurringPriceCents`) and schedules again (a pending upgrade once it is paid); a target tier without a 4-week plan is
  refused.
- `SubscriptionRenewalConversionService` (every `SUBSCRIPTIONS:RENEWAL_CONVERSION_INTERVAL_MINUTES`, default 60, off with
  `SUBSCRIPTIONS:RENEWAL_CONVERSION_ENABLED=false`) runs `SubscriptionService.ConvertDueRenewals`: active/past_due Stripe subscriptions
  of 13/52 weeks without a schedule, and scheduled ones within 3 days of their renewal (checked again), are fetched and scheduled. It
  converts the subscriptions sold before this change (no renewal metadata: renewal plan of their tier, priced like a plan switch from
  their creator code, discount code terms and checkout country) and retries webhook failures, which are only logged. Each run starts
  after a random delay of up to a quarter of the interval; a subscription whose change lease another replica or a webhook holds is
  skipped (debug log) and checked by the next run.
- Checkout page: Stripe shows the line item as "X per 52 weeks"; the line item's `product_data.description` therefore states the renewal
  as well ("First 52 weeks, then every 4 weeks at Y until cancelled.", `StripeSubscriptionDisclosure.LineItemTemplate`; not on Managed
  Payments' disallowed parameter list). 4-week plans keep the plan's description.
- If a 13/52 week Stripe subscription still renews for its full period (schedule missing), the payment is granted for that period
  (the buyer paid) and an error is logged: settle the difference by hand. The next run schedules it before its next renewal.
- PayPal: every plan made now is a TRIAL cycle of the plan's period then infinite 4-week REGULAR cycles (each PayPal billing cycle has
  its own `frequency`; developer.paypal.com/api/subscriptions/v1, `billing_cycle`), the REGULAR list price the renewal plan's price;
  a subscription overrides both prices (first price, renewal price). `PayPalPlans.RenewalWeeks` is part of the catalog key, so 13/52
  week plans made before (REGULAR = their own period) are never reused for checkouts, but subscriptions on them are still accepted.
  On activation a 13/52 week subscription on a 4-week-renewal plan gets its renewal terms (renewal price = the checkout's
  `RecurringAmount`); every sale after the first granted one converts it and must charge the renewal price.
- Existing PayPal 13/52 week subscriptions (on the old plans) can not be converted without the payer: `revise` (switch plan) "requires
  the buyer's consent", and `PATCH` can only change prices and `total_cycles`, not a cycle's frequency. They keep renewing for their
  period; the conversion run logs a warning listing the ones that renew within 30 days. Options: ask the payer to approve a revision to
  the new plan, or cancel at PayPal before the renewal (access runs out with the paid term) and offer a new subscription.
- Lemon Squeezy subscriptions are not changed (Lemon Squeezy is the seller there).
- Payment events (Kafka `sky-payments`, read by SkyEventBroker's purchase confirmation) of direct and Managed Payments subscription
  payments carry `SubscriptionBillingWeeks` (the term paid for: 4, 13 or 52; 4 after the conversion), `SubscriptionRenewalWeeks`
  (4, or the plan's own weeks for a legacy PayPal plan or a 13/52 week subscription without renewal terms), `SubscriptionRenewalAmount`
  (what each following cycle charges in the event's `Currency`: direct gross, Managed Payments net; null when a Managed Payments buyer
  paid in another currency) and `SubscriptionIsRenewal` (false for the first payment). Null on every other event (`SubscriptionEventTerms`).

## PayPal

Webhook endpoint `/callback/paypal` (id in `PAYPAL:WEBHOOK_ID`), events `BILLING.SUBSCRIPTION.*`, `PAYMENT.SALE.COMPLETED`,
`PAYMENT.SALE.REFUNDED`, `PAYMENT.SALE.REVERSED`. The REST app needs the Subscriptions feature enabled.

PayPal plans are created automatically on the first checkout of a (product slug, billing weeks, currency): a catalog product
(`SERVICE`, `SOFTWARE`) and a billing plan (`WEEK` interval) are created and the plan id is stored in the `PayPalPlans`
table (unique on slug+weeks+renewal weeks+currency; a concurrent first checkout keeps the stored plan, an orphan plan at PayPal is harmless).
Each plan is a one cycle TRIAL (sequence 1, 4/13/52 weeks) followed by an infinite REGULAR cycle (sequence 2, 4 weeks, see the
4-week renewal); every subscription overrides the price of both cycles, which is how `once` codes and the renewal price work.
`PAYPAL:SUBSCRIPTION_PLANS:{slug}` is still read as an optional override for 4-week plans (same TRIAL + REGULAR structure) and is no
longer required. It is ignored for 13 and 52 week plans (warning logged once): nothing proves the configured plan continues every 4 weeks.

**Deploying the 4-week renewal (and any rollout while 13/52 week plans of both kinds exist):** the release before it looks PayPal plans
up by (slug, weeks, currency) only. Once a new pod has created a 13/52-to-4-week plan, an old pod may pick it and price its REGULAR cycles
with the full 13/52 week price, so the buyer would be charged that every 4 weeks. Roll out without old and new pods serving at once:
use the `Recreate` strategy (or scale to one replica) for this deploy, or set `SUBSCRIPTIONS:PAYPAL_ENABLED=false` on the old
release before the rollout and back to true afterwards. Later releases are not affected (they look up by renewal weeks).
Each sale is granted only when it matches the agreed price: the first sale the first-cycle price, later sales the recurring
(13/52 weeks: the 4-week renewal) price.

## Database

`Migrations/20261117120348_DirectSubscriptions` (EF) and the hand-written `.cockroach.sql` beside it add the subscription columns,
`DiscountCodes` (with `PeriodAmounts` as a JSON text column and `AutoApply`), `PayPalPlans` and `PaymentRequests.RecurringAmount`, plus the
evidence columns `PaymentRecords.CheckoutCountry/BillingCountry/PaymentMethodCountry` and `ServicePerformanceDeclarations.PaymentRequestId`,
`Subscriptions.CheckoutCountry` and `CheckoutRegion` with their index `(CheckoutCountry, CheckoutRegion, Status)`, `PaymentRequests.CheckoutRegion`,
`PaymentRecords.CheckoutRegion/BillingRegion`, and the partial unique index `UX_CreatorCodeRevenues_Reversal` on
`CreatorCodeRevenues.TransactionReference` for reversal rows only (`LIKE '%#refund:%'`): sale rows written by earlier releases were never checked for
duplicates, so a full unique index could fail on them and they are never deleted; the script stops if reversal rows are already duplicated
(existing tables, so no new grants; `Migrations/apply-direct-subscriptions.sh` runs the same file per instance). Duplicate rows of a
user's subscription are removed (newest kept) before the provider backfill, comparing providers as backfilled (NULL/'' =
`lemonsqueezy`, like `--preview`): the old release keeps writing Lemon Squeezy rows without a provider until the deploy, and a rerun
would otherwise stop on the unique index when backfilling two of them.

`Migrations/20261118111600_FourWeekRenewal` (EF) and its `.cockroach.sql` (same idempotent style, apply right before the deploy;
`Migrations/apply-direct-subscriptions.sh` applies it after the two earlier ones, also on databases that already have those)
add `Subscriptions.RenewalProductId` (FK to `Product`, indexed), `RenewalPriceCents`, `RenewalScheduleId` and
`PayPalPlans.RenewalWeeks` (existing rows set to their `BillingWeeks`), and replace the PayPal plan unique index by one on
`(ProductSlug, BillingWeeks, RenewalWeeks, Currency)`. Rows the old release adds between the script and the deploy keep
`RenewalWeeks` 0, which the new release treats like the old plans.
