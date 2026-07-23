-- Chapter 18: the CRUD-shaped legacy order system the migration patterns read from.
--
-- This is the "before" side of the strangler: a plain relational schema with no events, plus a
-- change-tracking table the CDC pattern reads to emit domain events. Every statement is idempotent,
-- so the applier can run it against a fresh or an already-provisioned database with the same result.

CREATE SCHEMA IF NOT EXISTS legacy;

-- The order aggregate, CRUD-shaped: a single row updated in place, no history of its own.
CREATE TABLE IF NOT EXISTS legacy.orders (
    id            BIGINT   NOT NULL,
    customer_name TEXT     NOT NULL,
    status        TEXT     NOT NULL,
    total         NUMERIC  NOT NULL,
    CONSTRAINT pk_legacy_orders PRIMARY KEY (id)
);

-- The order's line items, the child table the CRUD shape carries. No trigger in this slice: CDC
-- tracks the order header, and widens to the lines only if a later pattern needs them.
CREATE TABLE IF NOT EXISTS legacy.order_lines (
    id       BIGINT  NOT NULL,
    order_id BIGINT  NOT NULL,
    sku      TEXT    NOT NULL,
    quantity INT     NOT NULL,
    CONSTRAINT pk_legacy_order_lines PRIMARY KEY (id),
    CONSTRAINT fk_legacy_order_lines_order FOREIGN KEY (order_id) REFERENCES legacy.orders (id)
);

-- The change-tracking table. Every insert, update, and delete on a tracked table appends one row
-- here in commit order, carrying the row image as JSONB. This is what the CDC reader consumes.
CREATE TABLE IF NOT EXISTS legacy.legacy_changes (
    change_id    BIGSERIAL    NOT NULL,
    table_name   TEXT         NOT NULL,
    operation    CHAR(1)      NOT NULL,
    record_id    BIGINT       NOT NULL,
    payload      JSONB        NOT NULL,
    occurred_utc TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_legacy_changes PRIMARY KEY (change_id)
);

-- The change recorder. TG_TABLE_NAME rather than a literal, so attaching this same function to
-- order_lines in a later slice needs no second function. Insert and update record the new row
-- image; delete records the old one.
CREATE OR REPLACE FUNCTION legacy.record_change() RETURNS TRIGGER AS $$
BEGIN
    IF (TG_OP = 'DELETE') THEN
        INSERT INTO legacy.legacy_changes (table_name, operation, record_id, payload)
        VALUES (TG_TABLE_NAME, 'D', OLD.id, to_jsonb(OLD));
        RETURN OLD;
    ELSIF (TG_OP = 'UPDATE') THEN
        INSERT INTO legacy.legacy_changes (table_name, operation, record_id, payload)
        VALUES (TG_TABLE_NAME, 'U', NEW.id, to_jsonb(NEW));
        RETURN NEW;
    ELSE
        INSERT INTO legacy.legacy_changes (table_name, operation, record_id, payload)
        VALUES (TG_TABLE_NAME, 'I', NEW.id, to_jsonb(NEW));
        RETURN NEW;
    END IF;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_orders_record_change ON legacy.orders;
CREATE TRIGGER trg_orders_record_change
    AFTER INSERT OR UPDATE OR DELETE ON legacy.orders
    FOR EACH ROW EXECUTE FUNCTION legacy.record_change();

-- The CDC reader's cursor: the highest change_id it has processed. One row, pinned to id = 1 by the
-- check, holds the high-water mark. The reader is at-least-once: it advances this only after the
-- events for a batch are appended, so a crash between the two replays the batch rather than dropping
-- it, and the append side does not dedupe a replay.
CREATE TABLE IF NOT EXISTS legacy.cdc_checkpoint (
    id             INT     NOT NULL,
    last_change_id BIGINT  NOT NULL,
    CONSTRAINT pk_cdc_checkpoint       PRIMARY KEY (id),
    CONSTRAINT ck_cdc_checkpoint_row   CHECK (id = 1)
);

INSERT INTO legacy.cdc_checkpoint (id, last_change_id)
VALUES (1, 0)
ON CONFLICT (id) DO NOTHING;
