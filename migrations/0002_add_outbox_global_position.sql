-- migrations/0002_add_outbox_global_position.sql
-- Session 0005: global_position on the outbox row.
-- event_store.outbox is already a denormalized dispatch envelope. It copies
-- event_type, payload, metadata, and occurred_utc from the events row at
-- append time. global_position joins that set so an outbox row is fully
-- self-describing: the OutboxProcessor never reads back into
-- event_store.events to assemble the dispatch context, and projections that
-- consume the dispatch can checkpoint on it.
-- PostgresEventStore.AppendAsync populates the column via INSERT ... RETURNING
-- from the events insert, inside the same transaction as the outbox insert.
-- NOT NULL is safe here: this migration only ever applies to a fresh database
-- right after 0001 created the empty table, and every outbox row from this
-- point forward is created with its event, so it always has a position.

ALTER TABLE event_store.outbox
    ADD COLUMN global_position BIGINT NOT NULL;
