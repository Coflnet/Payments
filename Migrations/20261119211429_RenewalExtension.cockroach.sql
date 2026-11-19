-- Apply after 20261118111600_FourWeekRenewal and right before deploying the renewal reminders and the "renew for another term"
-- extension of 13 and 52 week subscriptions.
-- Run with the existing migration authority and stop on the first error.
-- In cockroach sql, enable \set errexit before reading/pasting this file.
-- Rerunnable after interruption; do not wrap the whole script in a transaction.
-- Only nullable columns and an index of existing tables: no backfill, no new grants.

-- The term end the renewal reminder was published for (one reminder per term end)
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "RenewalReminderSentFor" timestamp with time zone;

-- The buyer's opt-in to one more 13 or 52 week term: the term end it starts at and what it charges
-- (direct: gross incl. VAT, Managed Payments: net)
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ExtensionStartsAt" timestamp with time zone;
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ExtensionPriceCents" bigint;

-- PayPal: the new PayPal subscription that continues this one (pending approval until linked) and, on that new row,
-- the subscription it continues, or when it was refused (its payments are never granted). One continuation per subscription.
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ExtensionSubscriptionId" character varying(64);
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "PreviousSubscriptionId" character varying(64);
ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ContinuationRefusedAt" timestamp with time zone;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Subscriptions_ExtensionSubscriptionId" ON "Subscriptions" ("ExtensionSubscriptionId");

-- PayPal extensions: the subscription a payment request extends (such requests are never reused as or counted like checkouts)
ALTER TABLE "PaymentRequests" ADD COLUMN IF NOT EXISTS "ExtendsSubscriptionId" character varying(64);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261119211429_RenewalExtension', '10.0.7')
ON CONFLICT ("MigrationId") DO NOTHING;

SHOW COLUMNS FROM "Subscriptions";
SHOW COLUMNS FROM "PaymentRequests";
SHOW INDEXES FROM "Subscriptions";
