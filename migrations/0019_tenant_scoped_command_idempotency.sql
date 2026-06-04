-- 0019_tenant_scoped_command_idempotency.sql
-- Phase 10 (multi-tenancy): make the command-idempotency key tenant-scoped under the shared-schema
-- isolation model. Migration 0007 keyed the table on idempotency_key alone; this migration adds the
-- tenant_id discriminator and swaps the primary key to composite (tenant_id, idempotency_key), the
-- tenant-leading order migration 0018 used for the SKU keys, so two tenants dedupe the same key
-- independently. A client-supplied key that two tenants legitimately share no longer collapses one
-- tenant's dispatch onto the other tenant's record, and the stream-derived process-manager keys gain
-- defense in depth.
--
-- Add-default-then-drop-default. The NOT NULL DEFAULT materializes the default tenant for every
-- pre-existing row at ALTER time, so there is no separate backfill UPDATE; the literal is
-- WellKnownTenants.Default, pinned in a checksummed migration because the legacy corpus keys off it
-- permanently and it must not drift. The default is then dropped: this table is on the command path,
-- so an insert that omits the tenant must raise a NOT NULL violation rather than silently write the
-- default tenant. The store binds the tenant on every insert, so the dropped default never fires in
-- normal operation; it is the schema-level backstop behind the behavior's fail-closed tenant read.
--
-- Safe on populated data with no rewrite and no empty-table guard: every pre-existing row carries the
-- default tenant, so (default, idempotency_key) is unique exactly when idempotency_key was unique as
-- the old single-column key. The primary-key swap takes ACCESS EXCLUSIVE and rebuilds the backing
-- index, a scale-bounded choice acceptable for this table's size; CREATE UNIQUE INDEX CONCURRENTLY
-- followed by a swap is the path if dispatch availability or table size later requires it.
--
-- ix_command_idempotency_processed_utc is unchanged. An event_store-schema migration, so it updates
-- the migration count and identity in PostgresMigrationRunnerTests.

ALTER TABLE event_store.command_idempotency
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

ALTER TABLE event_store.command_idempotency
    ALTER COLUMN tenant_id DROP DEFAULT;

ALTER TABLE event_store.command_idempotency
    DROP CONSTRAINT pk_command_idempotency,
    ADD CONSTRAINT pk_command_idempotency PRIMARY KEY (tenant_id, idempotency_key);
