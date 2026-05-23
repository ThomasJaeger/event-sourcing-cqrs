-- 0011_add_order_list_return_state.sql
-- Phase 6 (Cluster 2, ADR 0020): return state on the order-list read model.
-- ShipmentReturned carries only ShipmentId, so OrderListProjection cannot key
-- the order_list row from the return event. It records a ShipmentId-to-OrderId
-- mapping from ShipmentScheduled (which carries both) in order_list_shipments,
-- then resolves the OrderId on ShipmentReturned. The mapping persists: no later
-- event re-needs it and a kept row costs nothing.
--
-- is_returned/returned_utc are a parallel column pair, not a status value (D5):
-- a return is a Fulfillment fact, and the Sales status enum stays
-- Placed/Shipped/Cancelled/Completed. Queries compose the two visibly
-- (is_returned = false AND status = 'Shipped' for returnable orders).
-- A read_models-schema migration, so it does not touch the event_store
-- constraint and index assertions.

ALTER TABLE read_models.order_list
    ADD COLUMN is_returned  BOOLEAN     NOT NULL DEFAULT false,
    ADD COLUMN returned_utc TIMESTAMPTZ NULL;

CREATE TABLE read_models.order_list_shipments (
    shipment_id   UUID        NOT NULL,
    order_id      UUID        NOT NULL,
    scheduled_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_order_list_shipments PRIMARY KEY (shipment_id)
);
