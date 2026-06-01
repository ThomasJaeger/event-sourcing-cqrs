-- 0016_add_events_tenant_id.sql
-- Phase 10 (multi-tenancy): tenant_id on the events table as a STORED generated column, projecting the
-- tenant out of the metadata JSONB exactly as correlation_id and causation_id are projected in 0001, so
-- a later AdminConsole tenant filter can index on it without parsing JSONB at query time. COALESCE maps
-- a pre-tenancy row (no tenant_id key, or an explicit JSON null) to the default tenant, the column-side
-- mirror of the EventMetadataReader read seam. The default Guid is a frozen literal matching
-- WellKnownTenants.Default (ADR 0029, the typed TenantId); a generated-column expression cannot
-- reference a constant, and the value must never drift, so it is pinned here in a checksummed migration.
-- No empty-table guard: a STORED generated column is computed for every existing row at ALTER time, so
-- the COALESCE derives each legacy row's default from its own metadata with no backfill UPDATE. A guard
-- like 0006's would wrongly refuse to run on a populated database and defeat the additive strategy.
-- Unlike a read_models migration, this adds an event_store index (ix_events_tenant_id), so it updates
-- the event_store index assertion in PostgresMigrationRunnerTests.

ALTER TABLE event_store.events
    ADD COLUMN tenant_id UUID GENERATED ALWAYS AS
        (COALESCE((metadata->>'tenant_id')::uuid, '00000000-0000-0000-0000-000000000001'::uuid)) STORED;

CREATE INDEX ix_events_tenant_id
    ON event_store.events (tenant_id);
