-- 0009_add_sku_to_inventory_id_lookup.sql
-- Phase 5 (ADR 0017, Decision 9): SKU-to-InventoryId lookup for the
-- OrderFulfillment process manager. SkuToInventoryIdProjection populates it from
-- InventoryCreated; the PM reads it before each ReserveInventory dispatch to
-- resolve a line's SKU to the Inventory aggregate it must reserve against.
-- A read-model migration in the read_models schema (created in 0003), so it does
-- not touch the event_store migration assertions.

CREATE TABLE read_models.sku_to_inventory_id (
    sku           TEXT NOT NULL,
    -- inventory_id is UUID, the aggregate identifier in its raw-Guid form (ADR
    -- 0005), not the {prefix}:{guid} stream-id text form. The consumer is the
    -- process manager, which uses the Guid to target the Inventory aggregate
    -- through IEventStoreRepository<Inventory>, and that repository takes a Guid.
    inventory_id  UUID NOT NULL,
    CONSTRAINT pk_sku_to_inventory_id PRIMARY KEY (sku)
);
