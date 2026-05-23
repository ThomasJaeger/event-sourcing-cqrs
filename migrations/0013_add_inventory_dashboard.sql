-- 0013_add_inventory_dashboard.sql
-- Phase 6 (Cluster 4, D10): per-SKU inventory dashboard read model.
-- InventoryDashboardProjection derives on-hand quantity (a running sum of
-- InventoryAdjusted deltas) and reserved quantity (reserves minus releases)
-- from the Fulfillment inventory events. InventoryReleased carries no quantity
-- (the lean-compensating-event shape, ADR 0020), so the projection records a
-- per-reservation lookup row on InventoryReserved and recovers the quantity
-- from it on release.
--
-- inventory_dashboard is the public read model, keyed on inventory_id (the
-- per-SKU aggregate identity, 1:1 with SKU per the Phase 4 Inventory aggregate),
-- with sku UNIQUE so a query can resolve by SKU without the InventoryId.
-- inventory_reservations is projection-private state; the release lookup keys on
-- the full (inventory_id, order_id, line_id) PK, all three carried by
-- InventoryReleased, so no secondary index is needed. A read_models migration,
-- so it does not touch the event_store constraint/index assertions.

CREATE TABLE read_models.inventory_dashboard (
    inventory_id      UUID        NOT NULL,
    sku               TEXT        NOT NULL UNIQUE,
    on_hand_quantity  INTEGER     NOT NULL DEFAULT 0,
    reserved_quantity INTEGER     NOT NULL DEFAULT 0,
    last_updated_utc  TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_inventory_dashboard PRIMARY KEY (inventory_id)
);

CREATE TABLE read_models.inventory_reservations (
    inventory_id UUID        NOT NULL,
    order_id     UUID        NOT NULL,
    line_id      UUID        NOT NULL,
    quantity     INTEGER     NOT NULL,
    reserved_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_inventory_reservations PRIMARY KEY (inventory_id, order_id, line_id)
);
