-- Apply after SubscriptionTierSlots and before deploying subscription plan changes.
-- Run with the existing migration authority and stop on the first error.
-- In cockroach sql, enable \set errexit before reading/pasting this file.
-- Rerunnable after interruption; do not wrap the whole script in a transaction.

ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ChangeLockUntil" timestamp with time zone;

ALTER TABLE "Subscriptions" ADD COLUMN IF NOT EXISTS "ProviderVariantId" bigint;

ALTER TABLE "OwnerShip" ADD COLUMN IF NOT EXISTS "SubscriptionId" character varying(64);

ALTER TABLE "FiniteTransactions" ADD COLUMN IF NOT EXISTS "SubscriptionId" character varying(64);

CREATE TABLE IF NOT EXISTS "RefundedSubscriptionInvoices" (
    "InvoiceId" character varying(64) NOT NULL,
    "SubscriptionId" character varying(64),
    "BillingReason" text,
    CONSTRAINT "PK_RefundedSubscriptionInvoices" PRIMARY KEY ("InvoiceId")
);

CREATE TABLE IF NOT EXISTS "SubscriptionPlanChanges" (
    "Id" bigint NOT NULL DEFAULT unique_rowid(),
    "SubscriptionId" character varying(64),
    "PreviousProductId" integer,
    "ProductId" integer,
    "ChangedAt" timestamp with time zone NOT NULL,
    "PeriodEnd" timestamp with time zone NOT NULL,
    "InvoiceId" character varying(64),
    "Refunded" boolean NOT NULL,
    CONSTRAINT "PK_SubscriptionPlanChanges" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_SubscriptionPlanChanges_Product_PreviousProductId" FOREIGN KEY ("PreviousProductId") REFERENCES "Product" ("Id"),
    CONSTRAINT "FK_SubscriptionPlanChanges_Product_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Product" ("Id")
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OwnerShip_UserId_SubscriptionId" ON "OwnerShip" ("UserId", "SubscriptionId");

-- Keep user lookups indexed while the replacement is built.
-- Override safe updates only on this connection, then restore protection.
-- If interrupted between these statements, reconnect or SET sql_safe_updates = true.
SET sql_safe_updates = false;
DROP INDEX IF EXISTS "OwnerShip"@"IX_OwnerShip_UserId";
SET sql_safe_updates = true;

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SubscriptionPlanChanges_InvoiceId" ON "SubscriptionPlanChanges" ("InvoiceId");

CREATE INDEX IF NOT EXISTS "IX_SubscriptionPlanChanges_PreviousProductId" ON "SubscriptionPlanChanges" ("PreviousProductId");

CREATE INDEX IF NOT EXISTS "IX_SubscriptionPlanChanges_ProductId" ON "SubscriptionPlanChanges" ("ProductId");

CREATE INDEX IF NOT EXISTS "IX_SubscriptionPlanChanges_SubscriptionId_ChangedAt" ON "SubscriptionPlanChanges" ("SubscriptionId", "ChangedAt");

-- Grant access to the new tables before marking the migration complete.
GRANT SELECT, INSERT, UPDATE ON TABLE "SubscriptionPlanChanges" TO sky_payments;
GRANT SELECT, INSERT ON TABLE "RefundedSubscriptionInvoices" TO sky_payments;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260912131418_SubscriptionPlanLifecycle', '10.0.7')
ON CONFLICT ("MigrationId") DO NOTHING;

SHOW GRANTS ON TABLE "SubscriptionPlanChanges" FOR sky_payments;
SHOW GRANTS ON TABLE "RefundedSubscriptionInvoices" FOR sky_payments;
