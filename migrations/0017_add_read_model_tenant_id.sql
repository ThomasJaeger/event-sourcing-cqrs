-- 0017_add_read_model_tenant_id.sql
-- Phase 10 (multi-tenancy): the tenant_id discriminator on the read-model tables, under the
-- shared-schema isolation model (ADR 0031). Thirteen read_models tables gain the column: the four
-- public read models and the projection-private lookups whose keys resolve aggregate ids that are
-- not unique across tenants. Two tables stay out. projection_checkpoints (0003) is global
-- cross-tenant bookkeeping, since one projection consumes every tenant's events under a single
-- position, so a tenant on the checkpoint row would be wrong. current_user_roles (0015) defers its
-- tenant to the slice that resolves a per-user tenant on the principal.
--
-- The DEFAULT is the frozen WellKnownTenants.Default literal (ADR 0029, the typed TenantId), pinned
-- here in a checksummed migration because the legacy corpus keys off it permanently and it must not
-- drift across restarts or deployments, the same integrity note 0016 carries. A NOT NULL column
-- with a constant DEFAULT materializes the default tenant for every existing row at ALTER time, so
-- there is no separate backfill UPDATE. This is the read_models additive-column idiom (0011), not
-- the events-table generated-column form (0016): a read-model table has no metadata JSONB to
-- project a tenant from, so the value is a column DEFAULT rather than a generated expression.
--
-- Every table takes a bare column here and no key, constraint, or index changes. The two SKU-keyed
-- tables, the inventory dashboard and the SKU-to-inventory lookup, take the bare column too; their
-- tenant-scoped SKU uniqueness and primary key land with the projection-side tenant read and write
-- wiring, because a composite SKU key is inseparable from a tenant-aware lookup and upsert and is
-- meaningless until the read and write name the tenant.
--
-- A read_models-schema migration, so it does not touch the event_store constraint and index
-- assertions; it updates the migration count and identity in PostgresMigrationRunnerTests.

-- Public read model: the order-list view.
ALTER TABLE read_models.order_list
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: ShipmentId-to-OrderId for return-state resolution.
ALTER TABLE read_models.order_list_shipments
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Public read model: the per-customer summary.
ALTER TABLE read_models.customer_summary
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: per-order customer context for the cancel-side lookup.
ALTER TABLE read_models.customer_summary_orders
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: reservation quantities for the InventoryReleased lookup.
ALTER TABLE read_models.inventory_reservations
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Public read model: the order-detail header.
ALTER TABLE read_models.order_detail
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Public read model: the order-detail line items.
ALTER TABLE read_models.order_detail_lines
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Public read model: the order-detail event timeline.
ALTER TABLE read_models.order_detail_timeline
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: ShipmentId-to-OrderId for order-detail joins.
ALTER TABLE read_models.order_detail_shipments
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: PaymentId-to-OrderId for order-detail joins.
ALTER TABLE read_models.order_detail_payments
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: OrderId-to-PaymentId for the Return process manager.
ALTER TABLE read_models.order_id_to_payment_id
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Public read model: the per-SKU inventory dashboard. The tenant-scoped SKU uniqueness lands later.
ALTER TABLE read_models.inventory_dashboard
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;

-- Projection-private lookup: SKU-to-InventoryId for the OrderFulfillment process manager. The
-- tenant-scoped composite primary key lands later, with the tenant-aware lookup read and write.
ALTER TABLE read_models.sku_to_inventory_id
    ADD COLUMN tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000001'::uuid;
