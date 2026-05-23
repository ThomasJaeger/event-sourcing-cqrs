-- 0014_add_order_detail.sql
-- Phase 6 (Cluster 5, D1/D2/D9): the OrderDetail read model. OrderDetailProjection
-- subscribes to sixteen events across three contexts (eight Sales, four Fulfillment
-- shipment, four Billing) and maintains a header row, line items, and a JSONB event
-- timeline with one entry per observed event.
--
-- Two event groups carry only their own aggregate's id, not OrderId, and the read
-- model keys on OrderId. The three shipment-update events (ShipmentDispatched,
-- ShipmentDelivered, ShipmentReturned) carry ShipmentId only; three of the four
-- payment events (PaymentCaptured, PaymentRefunded, PaymentVoided) carry PaymentId
-- only. ADR 0020's rule applies to each: the projection keeps a private lookup,
-- populated by the event that carries both ids (ShipmentScheduled, PaymentAuthorized)
-- and queried by the events that carry one. Both lookups persist rather than
-- consume-and-delete: a shipment can dispatch, deliver, then return, and a payment
-- can capture then refund, with the follow-on events arriving indefinitely later, so
-- the mapping outlives any single use. (Contrast inventory_reservations, which is
-- consumed and deleted on the terminal InventoryReleased.)
--
-- order_detail, order_detail_lines, and order_detail_timeline are the public read
-- model; order_detail_shipments and order_detail_payments are projection-private.
-- status is TEXT (last-observed label, D6). The *_utc columns denormalise the
-- lifecycle timestamps so a query reads them without scanning the timeline. total and
-- the shipping address are nullable because they are not known until OrderPlaced and
-- ShippingAddressSet. A read_models migration, so it does not touch the event_store
-- constraint and index assertions.

CREATE TABLE read_models.order_detail (
    order_id                     UUID           NOT NULL,
    customer_id                  UUID           NOT NULL,
    status                       TEXT           NOT NULL,
    placed_utc                   TIMESTAMPTZ    NULL,
    shipped_utc                  TIMESTAMPTZ    NULL,
    cancelled_utc                TIMESTAMPTZ    NULL,
    completed_utc                TIMESTAMPTZ    NULL,
    returned_utc                 TIMESTAMPTZ    NULL,
    total_amount                 NUMERIC(18, 4) NULL,
    total_currency               TEXT           NULL,
    shipping_address_street      TEXT           NULL,
    shipping_address_city        TEXT           NULL,
    shipping_address_postal_code TEXT           NULL,
    shipping_address_country     TEXT           NULL,
    last_updated_utc             TIMESTAMPTZ    NOT NULL,
    CONSTRAINT pk_order_detail PRIMARY KEY (order_id)
);

CREATE TABLE read_models.order_detail_lines (
    order_id            UUID           NOT NULL,
    line_id             UUID           NOT NULL,
    sku                 TEXT           NOT NULL,
    quantity            INTEGER        NOT NULL,
    unit_price_amount   NUMERIC(18, 4) NOT NULL,
    unit_price_currency TEXT           NOT NULL,
    CONSTRAINT pk_order_detail_lines PRIMARY KEY (order_id, line_id)
);

CREATE TABLE read_models.order_detail_timeline (
    order_id        UUID        NOT NULL,
    global_position BIGINT      NOT NULL,
    event_type      TEXT        NOT NULL,
    occurred_utc    TIMESTAMPTZ NOT NULL,
    payload         JSONB       NOT NULL,
    CONSTRAINT pk_order_detail_timeline PRIMARY KEY (order_id, global_position)
);

CREATE TABLE read_models.order_detail_shipments (
    shipment_id   UUID        NOT NULL,
    order_id      UUID        NOT NULL,
    scheduled_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_order_detail_shipments PRIMARY KEY (shipment_id)
);

CREATE TABLE read_models.order_detail_payments (
    payment_id     UUID        NOT NULL,
    order_id       UUID        NOT NULL,
    authorized_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_order_detail_payments PRIMARY KEY (payment_id)
);
