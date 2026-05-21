-- 0008_add_delayed_commands.sql
--
-- Delay queue for scheduled commands (ADR 0017). A process manager schedules a
-- timeout command to fire at fire_at_utc; DelayQueueProcessor (commit 17) claims
-- due rows with FOR UPDATE SKIP LOCKED (the row lock is the claim, no claimed_at
-- column) and dispatches them through ICausedCommandBus. The dispatch-metadata
-- columns (correlation_id through idempotency_key) are exactly the values
-- CausedCommandBus needs to rebuild the command context, so the row is
-- self-describing.

CREATE TABLE event_store.delayed_commands (
    delayed_command_id     BIGINT       GENERATED ALWAYS AS IDENTITY,
    fire_at_utc            TIMESTAMPTZ  NOT NULL,
    command_type           TEXT         NOT NULL,
    command_payload        JSONB        NOT NULL,
    correlation_id         UUID         NOT NULL,
    causation_id           UUID         NOT NULL,
    actor_id               UUID         NOT NULL,
    service_name           TEXT         NOT NULL,
    idempotency_key        TEXT         NOT NULL,
    scheduled_by_stream_id TEXT         NOT NULL,
    scheduled_by_step      TEXT         NOT NULL,
    scheduled_utc          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    dispatched_at_utc      TIMESTAMPTZ  NULL,
    cancelled_at_utc       TIMESTAMPTZ  NULL,
    cancellation_reason    TEXT         NULL,
    attempt_count          INT          NOT NULL DEFAULT 0,
    last_error             TEXT         NULL,
    next_attempt_at        TIMESTAMPTZ  NULL,
    CONSTRAINT pk_delayed_commands PRIMARY KEY (delayed_command_id)
);

-- Due-row scan: partial on the active set so the index stays proportional to
-- pending work rather than total history, the same shape as ix_outbox_pending.
CREATE INDEX ix_delayed_commands_due
    ON event_store.delayed_commands (fire_at_utc)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;

-- Cancellation lookup: process managers cancel by (stream, step); partial for
-- the same reason as the due index.
CREATE INDEX ix_delayed_commands_by_stream
    ON event_store.delayed_commands (scheduled_by_stream_id, scheduled_by_step)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;

-- Quarantine: terminal store for rows whose dispatch exhausted its retries,
-- reached by an atomic CTE move in DelayQueueProcessor (commit 17), mirroring
-- outbox_quarantine. No extra index: queries here are operational, not hot, and
-- one is added in a future migration if tooling needs it.
CREATE TABLE event_store.delayed_commands_quarantine (
    quarantine_id          BIGINT       GENERATED ALWAYS AS IDENTITY,
    delayed_command_id     BIGINT       NOT NULL,
    fire_at_utc            TIMESTAMPTZ  NOT NULL,
    command_type           TEXT         NOT NULL,
    command_payload        JSONB        NOT NULL,
    correlation_id         UUID         NOT NULL,
    causation_id           UUID         NOT NULL,
    actor_id               UUID         NOT NULL,
    service_name           TEXT         NOT NULL,
    idempotency_key        TEXT         NOT NULL,
    scheduled_by_stream_id TEXT         NOT NULL,
    scheduled_by_step      TEXT         NOT NULL,
    attempt_count          INT          NOT NULL,
    final_error            TEXT         NOT NULL,
    quarantined_at         TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_delayed_commands_quarantine PRIMARY KEY (quarantine_id)
);

-- LISTEN/NOTIFY wakeup mirroring migration 0005's outbox trigger. Fires once
-- per statement so a batched insert emits a single notification. Dormant until
-- DelayQueueProcessor (commit 17) listens on the channel. Less load-bearing
-- than the outbox's: most delay rows are future-dated, so the processor's
-- fallback timer carries the load and this mainly shortens latency for
-- at-or-near-present rows (ADR 0017).
CREATE FUNCTION event_store.notify_delayed_command_pending() RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('delayed_commands_pending', '');
    RETURN NULL;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_delayed_commands_notify_pending
    AFTER INSERT ON event_store.delayed_commands
    FOR EACH STATEMENT
    EXECUTE FUNCTION event_store.notify_delayed_command_pending();
