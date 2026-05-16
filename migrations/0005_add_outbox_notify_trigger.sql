-- migrations/0005_add_outbox_notify_trigger.sql
-- Session 0006, Chapter 8: LISTEN/NOTIFY wakeup for the outbox processor.
-- An AFTER INSERT trigger fires once per statement (not per row) so a batched
-- AppendAsync that writes N events emits a single notification. pg_notify
-- delivers at COMMIT time, so a listener only sees the channel signal after
-- the outbox row is visible to other transactions; ordering falls out of the
-- single-transaction AppendAsync shape. The notification payload is empty;
-- the processor's reaction is to wake and run its next batch query against
-- the table, not to read row data out of the channel.

CREATE FUNCTION event_store.notify_outbox_pending() RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('outbox_pending', '');
    -- AFTER STATEMENT triggers: the return value is ignored by PostgreSQL.
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_outbox_notify_pending
    AFTER INSERT ON event_store.outbox
    FOR EACH STATEMENT
    EXECUTE FUNCTION event_store.notify_outbox_pending();
