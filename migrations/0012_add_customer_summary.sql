-- 0012_add_customer_summary.sql
-- Phase 6 (Cluster 3, ADR 0019): per-customer summary read model.
-- CustomerSummaryProjection accumulates order_count, lifetime_value, and
-- last_order_utc from OrderPlaced, and nets cancellations back out on
-- OrderCancelled. OrderCancelled carries only OrderId (no CustomerId, no
-- total), so the projection recovers them from a projection-private per-order
-- lookup row recorded on placement.
--
-- customer_summary is the public read model; customer_summary_orders is
-- projection-private state (no store port exposes it, no query handler reads
-- it). The order_id index supports the cancel-side lookup, since the PK is
-- (customer_id, order_id) and OrderCancelled carries only the order id.
-- last_order_utc is not recomputed on cancellation (ADR 0019). A read_models
-- migration, so it does not touch the event_store constraint/index assertions.

CREATE TABLE read_models.customer_summary (
    customer_id             UUID           NOT NULL,
    order_count             INTEGER        NOT NULL DEFAULT 0,
    lifetime_value_amount   NUMERIC(18, 4) NOT NULL DEFAULT 0,
    lifetime_value_currency TEXT           NOT NULL,
    last_order_utc          TIMESTAMPTZ    NOT NULL,
    last_updated_utc        TIMESTAMPTZ    NOT NULL,
    CONSTRAINT pk_customer_summary PRIMARY KEY (customer_id)
);

CREATE TABLE read_models.customer_summary_orders (
    customer_id    UUID           NOT NULL,
    order_id       UUID           NOT NULL,
    total_amount   NUMERIC(18, 4) NOT NULL,
    total_currency TEXT           NOT NULL,
    placed_utc     TIMESTAMPTZ    NOT NULL,
    CONSTRAINT pk_customer_summary_orders PRIMARY KEY (customer_id, order_id)
);

-- The cancel-side lookup keys on order_id alone (OrderCancelled carries no
-- customer id), so the (customer_id, order_id) PK alone would force a scan.
CREATE INDEX ix_customer_summary_orders_order_id
    ON read_models.customer_summary_orders (order_id);
