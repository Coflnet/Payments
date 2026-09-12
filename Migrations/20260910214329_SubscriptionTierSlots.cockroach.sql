-- Apply after 20260907153456_OwnerManagedTierSlots, before deploying subscription slot support.
-- Run each statement separately with the existing migration authority.

ALTER TABLE "TierSlots" ADD "SubscriptionId" character varying(64);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260910214329_SubscriptionTierSlots', '10.0.7');
