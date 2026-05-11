-- migrations/0001_initial_event_store.sql
-- Chapter 8: Event Store schema (Figure 8.2) and Outbox Pattern (Figure 8.4).
-- Divergences from the figures are recorded in
-- docs/sessions/0002-weeks3-4-postgres-adapter.md as Track A flags 1-10.
-- PostgreSQL 16 syntax exclusive: IDENTITY columns, STORED generated columns, JSONB.

CREATE SCHEMA event_store;

-- Events: append-only, ordered by global_position for replay and projections.
-- correlation_id and causation_id are projected from metadata as STORED
-- generated columns so the AdminConsole correlation tracer can index on them
-- without parsing JSONB at query time.
CREATE TABLE event_store.events (
    global_position BIGINT       GENERATED ALWAYS AS IDENTITY,
    stream_id       UUID         NOT NULL,
    stream_version  INT          NOT NULL,
    event_id        UUID         NOT NULL,
    event_type      TEXT         NOT NULL,
    event_version   SMALLINT     NOT NULL,
    payload         JSONB        NOT NULL,
    metadata        JSONB        NOT NULL,
    occurred_utc    TIMESTAMPTZ  NOT NULL,
    correlation_id  UUID GENERATED ALWAYS AS ((metadata->>'correlation_id')::uuid) STORED,
    causation_id    UUID GENERATED ALWAYS AS ((metadata->>'causation_id')::uuid) STORED,
    CONSTRAINT pk_events                PRIMARY KEY (global_position),
    CONSTRAINT uq_events_stream_version UNIQUE (stream_id, stream_version),
    CONSTRAINT uq_events_event_id       UNIQUE (event_id)
);

CREATE INDEX ix_events_correlation_id
    ON event_store.events (correlation_id);

-- Outbox: transient dispatch envelopes drained by OutboxProcessor in FIFO
-- outbox_id order. destination is nullable for the v1 single-bus path;
-- future routing can populate it without a schema change.
-- Only correlation_id is projected as a generated column. Causation tracing
-- is a stream-level concern on the events table, not a dispatch-level concern.
CREATE TABLE event_store.outbox (
    outbox_id       BIGINT       GENERATED ALWAYS AS IDENTITY,
    event_id        UUID         NOT NULL,
    event_type      TEXT         NOT NULL,
    payload         JSONB        NOT NULL,
    metadata        JSONB        NOT NULL,
    destination     TEXT         NULL,
    occurred_utc    TIMESTAMPTZ  NOT NULL,
    sent_utc        TIMESTAMPTZ  NULL,
    attempt_count   INT          NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ  NULL,
    last_error      TEXT         NULL,
    correlation_id  UUID GENERATED ALWAYS AS ((metadata->>'correlation_id')::uuid) STORED,
    CONSTRAINT pk_outbox          PRIMARY KEY (outbox_id),
    CONSTRAINT uq_outbox_event_id UNIQUE (event_id)
);

-- Partial index on outbox_id (not on sent_utc) so the processor scans
-- pending rows in FIFO order without indexing a column that is always
-- NULL within the filter. Figure 8.4 has this inverted; Track A flag 9.
CREATE INDEX ix_outbox_pending
    ON event_store.outbox (outbox_id)
    WHERE sent_utc IS NULL;

CREATE INDEX ix_outbox_correlation
    ON event_store.outbox (correlation_id);

-- Outbox quarantine: poison-message terminus, separate table so the
-- live outbox stays small and the partial index stays cheap. No FK to
-- event_store.outbox: the live outbox row is removed on move, so an FK
-- would block pruning. outbox_id is kept as a historical reference.
CREATE TABLE event_store.outbox_quarantine (
    quarantine_id   BIGINT       GENERATED ALWAYS AS IDENTITY,
    outbox_id       BIGINT       NOT NULL,
    event_id        UUID         NOT NULL,
    event_type      TEXT         NOT NULL,
    payload         JSONB        NOT NULL,
    metadata        JSONB        NOT NULL,
    occurred_utc    TIMESTAMPTZ  NOT NULL,
    attempt_count   INT          NOT NULL,
    final_error     TEXT         NOT NULL,
    quarantined_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_outbox_quarantine PRIMARY KEY (quarantine_id)
);

-- Schema migrations: MigrationRunner reads version and checksum on each
-- run to decide what to apply and to detect post-application edits.
CREATE TABLE event_store.schema_migrations (
    version     INT          NOT NULL,
    name        TEXT         NOT NULL,
    applied_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    checksum    TEXT         NOT NULL,
    CONSTRAINT pk_schema_migrations PRIMARY KEY (version)
);
