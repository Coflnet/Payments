# Four-slot Premium bundles

The product catalog lives in `../k8s/main/payments/chart/charts/product-provisioner/values.yaml`.

| Product slug | Tier | Slots | Price | Period |
| --- | --- | --- | --- | --- |
| `premium_plus-slots-4` | Premium+ | 4 | 27,000 CoflCoins | 28 days |
| `l_prem_plus-slots-4` | Premium+ | 4 | EUR 99.69 | Every 4 weeks |
| `l_premium-slots-4` | Premium | 4 | EUR 33.69 | Every 4 weeks |

Subscription top-up and purchase transactions offset one another: their internal coin amounts are 27,000 and 7,200 respectively. The subscriber receives four seats, not a spendable coin balance.

## Purchase and assignment

- Coin purchase: `POST /api/service/purchase` with `slug: "premium_plus-slots-4"`, `count: 1`, and the existing checkout declaration fields. Omit `slotIds` to create four seats; send four distinct owned Premium+ slot IDs to extend them.
- Subscription checkout: `POST /api/premium/subscription/l_prem_plus-slots-4` or `POST /api/premium/subscription/l_premium-slots-4`.
- List owned seats: `GET /api/premium/slots`.
- Assign a seat: `PUT /api/premium/slots/{id}/assignment` with `email`, optional `minecraftAccount`, and the current `version`. The owner can assign a seat to their own email. Empty recipient fields release the seat.

New seats start unassigned. A subscription's first paid event stores its Lemon Squeezy ID on all four seats. Subsequent payments extend those same seats, including expired or released seats, preserving assignments. Separate subscriptions of the same product remain independent. Slot subscriptions do not enable free trials.

Existing personal Premium products continue to extend ordinary ownership. Coin bundles and subscription bundles are separate purchases; subscribing does not automatically adopt existing coin-purchased seats.

## Rollout

1. Apply the `SubscriptionTierSlots` database migration after `OwnerManagedTierSlots`. A CockroachDB SQL equivalent is included beside the EF migration.
2. Deploy Payments with subscription slot renewal support and SkyApi with the expanded top-up product listing.
3. Provision the updated product catalog. The proposed three-slot package is replaced by `premium_plus-slots-4`; other existing products retain their definitions.

Checkout uses the existing four-week Lemon Squeezy variant selection and sends the catalog amount as `custom_price`. Lemon Squeezy documents that this price also applies to renewals: https://docs.lemonsqueezy.com/api/checkouts/create-checkout#custom_price.

Local regression tests exercise coin pricing, self-assignment, recurring payments, assignment preservation, duplicate notifications, and independent subscriptions. Live checkout and database rollout require the deployed services and provider configuration.
