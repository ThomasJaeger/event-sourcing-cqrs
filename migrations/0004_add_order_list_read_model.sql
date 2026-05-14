-- migrations/0004_add_order_list_read_model.sql
-- Session 0005, Chapter 13: the OrderListProjection's read model.
-- A list view of placed orders: one row per order, inserted when the order is
-- placed and updated when it ships or is cancelled. The cart-phase events
-- (OrderDrafted, OrderLineAdded, OrderLineRemoved, ShippingAddressSet) precede
-- OrderPlaced and are not list-relevant, so the projection skips them; a row
-- never exists for an order cancelled while still a draft.
-- total_amount and total_currency are the two-column decomposition of the
-- domain's Money value: WHERE and ORDER BY stay simple relational predicates,
-- with no composite-type knowledge in the read path.

CREATE TABLE read_models.order_list (
    order_id          UUID           NOT NULL,
    customer_id       UUID           NOT NULL,
    status            TEXT           NOT NULL,
    total_amount      NUMERIC(18, 4) NOT NULL,
    total_currency    TEXT           NOT NULL,
    placed_utc        TIMESTAMPTZ    NOT NULL,
    last_updated_utc  TIMESTAMPTZ    NOT NULL,
    CONSTRAINT pk_order_list PRIMARY KEY (order_id)
);

-- "All orders, most recent first."
CREATE INDEX ix_order_list_placed_utc
    ON read_models.order_list (placed_utc DESC);

-- "Orders with a given status, most recent first." Compound rather than a bare
-- index on the low-cardinality status column, and deliberately not partial: a
-- partial predicate would pin the index to one UI's notion of an active order.
CREATE INDEX ix_order_list_status_placed_utc
    ON read_models.order_list (status, placed_utc DESC);
