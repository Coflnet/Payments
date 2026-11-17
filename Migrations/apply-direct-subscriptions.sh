#!/usr/bin/env bash
# Applies 20261022112747_PaymentRequestCoinAmount and 20261117120348_DirectSubscriptions to one or
# more Payments CockroachDB databases. Idempotent: every statement tolerates a rerun, and a database
# that already has both migrations is left unchanged (apart from re-issued GRANTs).
#
# Usage:
#   ./apply-direct-subscriptions.sh [--preview] <instance>=<cockroach url> [<instance>=<url> ...]
#   e.g. ./apply-direct-subscriptions.sh --preview sky="postgresql://root@host:26257/sky_payments?sslmode=verify-full&..."
#
# <instance> is sky, ane, dash or tab; table grants go to the instance's SQL user <instance>_payments.
# The url must use the migration authority (a user allowed to ALTER, CREATE and GRANT).
# --preview changes nothing: it lists the duplicate subscription rows the migration would delete and
# any subscription recorded for several users (which stops the migration until resolved by hand).
#
# Before deleting duplicates the script keeps a copy of "Subscriptions" in
# "Subscriptions_backup_20261117" (created once; a rerun keeps the first copy).
set -euo pipefail

MIGRATIONS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COIN_AMOUNT_SQL="$MIGRATIONS_DIR/20261022112747_PaymentRequestCoinAmount.cockroach.sql"
DIRECT_SQL="$MIGRATIONS_DIR/20261117120348_DirectSubscriptions.cockroach.sql"
KNOWN_INSTANCES="sky ane dash tab"

usage() {
    sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 2
}

# Stops (failing cast) unless the migration this one builds on is recorded. The cast sits in a CASE over
# a subquery: a constant cast under WHERE would be folded at plan time and fail even when the row exists.
prerequisite_sql() {
    cat <<'SQL'
SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260912131418_SubscriptionPlanLifecycle')
  THEN '0' ELSE 'STOP: 20260912131418_SubscriptionPlanLifecycle is not applied; apply the earlier migrations first' END AS INT8);
SQL
}

backup_sql() {
    cat <<'SQL'
CREATE TABLE IF NOT EXISTS "Subscriptions_backup_20261117" AS SELECT * FROM "Subscriptions";
SQL
}

# Compares providers the way they will be after the migration backfills NULL/'' to 'lemonsqueezy'.
preview_sql() {
    cat <<'SQL'
SELECT 'subscription recorded for several users (blocks the migration)' AS problem, s."ProviderSlug", s."ExternalId", s."UserId"
FROM "Subscriptions" s JOIN "Subscriptions" o
  ON coalesce(nullif(s."ProviderSlug", ''), 'lemonsqueezy') = coalesce(nullif(o."ProviderSlug", ''), 'lemonsqueezy')
 AND s."ExternalId" = o."ExternalId" AND s."UserId" <> o."UserId";
SELECT DISTINCT 'duplicate row that would be deleted' AS problem, s."Id", s."ProviderSlug", s."ExternalId", s."UserId", s."Status", s."UpdatedAt"
FROM "Subscriptions" s JOIN "Subscriptions" newer
  ON coalesce(nullif(s."ProviderSlug", ''), 'lemonsqueezy') = coalesce(nullif(newer."ProviderSlug", ''), 'lemonsqueezy')
 AND s."ExternalId" = newer."ExternalId" AND s."UserId" = newer."UserId"
 AND (s."UpdatedAt" < newer."UpdatedAt" OR (s."UpdatedAt" = newer."UpdatedAt" AND s."Id" < newer."Id"));
SQL
}

# The checked-in scripts grant to sky_payments; each instance has its own SQL user.
migration_sql() {
    local role="$1"
    prerequisite_sql
    cat "$COIN_AMOUNT_SQL"
    backup_sql
    sed "s/\bsky_payments\b/${role}/g" "$DIRECT_SQL"
}

run_sql() {
    local url="$1"
    cockroach sql --url "$url" --set=errexit=true --format=table
}

apply_instance() {
    local instance="$1" url="$2" preview="$3"
    echo "== $instance (${instance}_payments)"
    if [[ "$preview" == true ]]; then
        preview_sql | run_sql "$url"
        return
    fi
    migration_sql "${instance}_payments" | run_sql "$url"
    echo "== $instance done"
}

validate_target() {
    local instance="$1" url="$2"
    [[ " $KNOWN_INSTANCES " == *" $instance "* ]] || { echo "Unknown instance '$instance' (expected one of: $KNOWN_INSTANCES)" >&2; exit 2; }
    [[ -n "$url" ]] || { echo "Missing url for '$instance'" >&2; exit 2; }
}

main() {
    local preview=false
    if [[ "${1:-}" == "--preview" ]]; then
        preview=true
        shift
    fi
    [[ $# -gt 0 ]] || usage
    command -v cockroach >/dev/null || { echo "The cockroach CLI is required" >&2; exit 2; }

    local target
    for target in "$@"; do
        validate_target "${target%%=*}" "${target#*=}"
    done
    for target in "$@"; do
        apply_instance "${target%%=*}" "${target#*=}" "$preview"
    done
}

main "$@"
