-- Apply after 20261120121323_TermSwitch and before deploying Google Play subscriptions.
-- Run with the existing migration authority and stop on the first error.
-- In cockroach sql, enable \set errexit before reading/pasting this file.
-- Rerunnable after interruption; do not wrap the whole script in a transaction.
-- Only a nullable column of an existing table: no backfill or new grants.

ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ProviderToken" text;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261121134048_GooglePlaySubscriptions', '10.0.7')
ON CONFLICT ("MigrationId") DO NOTHING;

SHOW COLUMNS FROM "Subscriptions";
