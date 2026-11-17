-- Apply after SubscriptionPlanLifecycle and before deploying the PayPal order verification
-- that reads PaymentRequests.CoinAmount (webhook refuses PayPal requests without it).
-- Run with the existing migration authority and stop on the first error.
-- In cockroach sql, enable \set errexit before reading/pasting this file.
-- Rerunnable after interruption; do not wrap the whole script in a transaction.

ALTER TABLE "PaymentRequests" ADD COLUMN IF NOT EXISTS "CoinAmount" numeric;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261022112747_PaymentRequestCoinAmount', '10.0.7')
ON CONFLICT ("MigrationId") DO NOTHING;

SHOW COLUMNS FROM "PaymentRequests";
