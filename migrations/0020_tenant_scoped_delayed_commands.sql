-- 0020_tenant_scoped_delayed_commands.sql
-- Phase 10 (multi-tenancy): add the tenant_id discriminator to the delay-queue tables under the
-- shared-schema isolation model. A scheduled timeout carries the tenant of the event that caused it,
-- so the resurfaced command dispatches under that tenant rather than the default.
--
-- Keep-default, unlike the command-idempotency table of migration 0019. The delay-queue write is an
-- off-command-path worker write: ScheduleAsync always names the tenant from the always-present causing
-- metadata, so the NOT NULL DEFAULT is the deploy-safe honest value that materializes the default
-- tenant for every pre-existing row at ALTER time, with no separate backfill UPDATE. The default is
-- kept rather than dropped because a delay-queue insert that reached the column with no tenant is a
-- worker write where the default tenant is the honest fallback, not the command-path fail-closed
-- backstop migration 0019 needs. The quarantine companion takes the same column with the same default,
-- so a quarantined row keeps the tenant of the command it carried and the atomic move stays valid even
-- though it does not name the column.
--
-- The partial indexes, the notify trigger, and all constraints are unchanged. No index on tenant_id:
-- the due-row scan filters on fire_at_utc and the processor drains every due row regardless of tenant,
-- so there is no per-tenant predicate to support. An event_store-schema migration, so it updates the
-- migration count and identity in PostgresMigrationRunnerTests.

ALTER TABLE event_store.delayed_commands
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

ALTER TABLE event_store.delayed_commands_quarantine
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;
