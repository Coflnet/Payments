-- Apply after 20261117120348_DirectSubscriptions and right before deploying the 4-week renewal of 13 and 52 week subscriptions.
-- Run with the existing migration authority and stop on the first error.
-- In cockroach sql, enable \set errexit before reading/pasting this file.
-- Rerunnable after interruption; do not wrap the whole script in a transaction.
-- Only columns and indexes of existing tables: no new grants.

-- The 4-week plan a 13 or 52 week subscription continues as after its first term, what each 4-week renewal charges
-- (direct: gross incl. VAT, Managed Payments: net) and the Stripe subscription schedule that switches it.
-- The FK separately: CockroachDB checks a constraint named inside ADD COLUMN IF NOT EXISTS even when the column exists.
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalProductId" integer;
ALTER TABLE "Subscriptions" ADD CONSTRAINT IF NOT EXISTS "FK_Subscriptions_Product_RenewalProductId"
    FOREIGN KEY ("RenewalProductId") REFERENCES "Product" ("Id");
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalPriceCents" bigint;
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalScheduleId" character varying(64);
CREATE INDEX IF NOT EXISTS "IX_Subscriptions_RenewalProductId" ON "Subscriptions" ("RenewalProductId");

-- PayPal plans: the period of the REGULAR cycles. New plans continue every 4 weeks; the plans made so far repeat their
-- billing period. Rows the running (old) release adds after this point keep 0: the new release treats them like the old
-- plans (accepted for their subscriptions, never reused for a checkout, not "every 4 weeks"), so no rerun is needed.
ALTER TABLE "PayPalPlans" ADD COLUMN IF NOT EXISTS "RenewalWeeks" integer NOT NULL DEFAULT 0;
UPDATE "PayPalPlans" SET "RenewalWeeks" = "BillingWeeks" WHERE "RenewalWeeks" = 0;

-- One plan per product, period, renewal period and currency. The new unique index exists before the old one is dropped.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayPalPlans_ProductSlug_BillingWeeks_RenewalWeeks_Currency"
    ON "PayPalPlans" ("ProductSlug", "BillingWeeks", "RenewalWeeks", "Currency");
DROP INDEX IF EXISTS "PayPalPlans"@"IX_PayPalPlans_ProductSlug_BillingWeeks_Currency";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261118111600_FourWeekRenewal', '10.0.7')
ON CONFLICT ("MigrationId") DO NOTHING;

SHOW COLUMNS FROM "Subscriptions";
SHOW INDEXES FROM "PayPalPlans";
