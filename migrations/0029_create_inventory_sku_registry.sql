-- Write-side uniqueness shared by all four event-store providers. It lives in the
-- companion PostgreSQL database and must never be cleared by a projection rebuild.
CREATE SCHEMA IF NOT EXISTS write_side;

CREATE TABLE write_side.inventory_sku_claims (
    tenant_id UUID NOT NULL,
    sku TEXT NOT NULL,
    inventory_id UUID NOT NULL,
    PRIMARY KEY (tenant_id, sku),
    UNIQUE (tenant_id, inventory_id)
);

CREATE TABLE write_side.inventory_sku_registry_state (
    singleton BOOLEAN PRIMARY KEY CHECK (singleton),
    initialized BOOLEAN NOT NULL DEFAULT FALSE
);

INSERT INTO write_side.inventory_sku_registry_state (singleton) VALUES (TRUE);
