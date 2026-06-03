-- 0018_tenant_scoped_sku_uniqueness.sql
-- Phase 10 (multi-tenancy): make the two SKU-keyed read-model keys tenant-scoped under the
-- shared-schema isolation model. Migration 0017 added the tenant_id column to both tables but
-- left their SKU keys global; this migration swaps them to composite (tenant_id, sku) so two
-- tenants can each map the same SKU.
--
-- inventory_dashboard: the anonymous column UNIQUE(sku) from 0013 (PostgreSQL's default name
-- inventory_dashboard_sku_key) becomes a named UNIQUE(tenant_id, sku). sku_to_inventory_id: the
-- single-column primary key pk_sku_to_inventory_id (sku) from 0009 becomes the composite primary
-- key (tenant_id, sku).
--
-- Safe on populated data with no rewrite and no empty-table guard: every pre-existing row carries
-- the default tenant from 0017's NOT NULL DEFAULT, so a single-tenant corpus has one row per sku
-- all under the default tenant, which the composite key admits unchanged. It assumes it runs
-- before a second tenant is onboarded to these tables: until onboarding, the projection write
-- path tags both tables with the default tenant (the same current-tenant accessor the four public
-- read models use), so no existing row violates the composite key at apply time.
--
-- A read_models-schema migration, so it does not touch the event_store constraint and index
-- assertions; it updates the migration count and identity in PostgresMigrationRunnerTests.

ALTER TABLE read_models.inventory_dashboard
    DROP CONSTRAINT inventory_dashboard_sku_key,
    ADD CONSTRAINT uq_inventory_dashboard_tenant_sku UNIQUE (tenant_id, sku);

ALTER TABLE read_models.sku_to_inventory_id
    DROP CONSTRAINT pk_sku_to_inventory_id,
    ADD CONSTRAINT pk_sku_to_inventory_id PRIMARY KEY (tenant_id, sku);
