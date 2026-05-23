-- 0010_add_order_id_to_payment_id_lookup.sql
-- Phase 5 (Decision 12, commit 26a): OrderId-to-PaymentId lookup for the
-- ReturnProcessManager. OrderIdToPaymentIdProjection populates it from
-- PaymentAuthorized; the Return PM reads it before dispatching VoidPayment to
-- resolve a returned order to its payment, rather than reading the
-- OrderFulfillment process manager's internal state. Cross-PM data acquisition
-- goes through a read model; the cross-context-vocabulary doc names this pattern.
-- A read-model migration in the read_models schema (created in 0003), so it does
-- not touch the event_store migration assertions.

CREATE TABLE read_models.order_id_to_payment_id (
    order_id    UUID NOT NULL,
    -- payment_id is the Payment aggregate identifier in raw-Guid form (ADR 0005),
    -- which the Return PM passes to VoidPayment to target the Payment aggregate.
    payment_id  UUID NOT NULL,
    CONSTRAINT pk_order_id_to_payment_id PRIMARY KEY (order_id)
);
