-- 0007_add_command_idempotency.sql
--
-- Command idempotency store for the IdempotencyBehavior pipeline behavior
-- (ADR 0016). A command dispatched with an idempotency key records the key
-- here after its handler succeeds; a later dispatch with the same key is a
-- duplicate and short-circuits. The primary key is what the
-- eager-check-with-lazy-fallback pattern leans on: a concurrent second insert
-- of the same key violates it, and the behavior reads that as a duplicate.

CREATE TABLE event_store.command_idempotency (
    idempotency_key TEXT         NOT NULL,
    command_type    TEXT         NOT NULL,
    processed_utc   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_command_idempotency PRIMARY KEY (idempotency_key)
);

-- processed_utc index supports time-based retention (pruning or partitioning)
-- later; retention itself is deferred (ADR 0016).
CREATE INDEX ix_command_idempotency_processed_utc
    ON event_store.command_idempotency (processed_utc);
