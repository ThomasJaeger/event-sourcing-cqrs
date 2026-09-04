-- migrations/sqlserver/0002_add_outbox.sql
-- Chapter 8: the Outbox Pattern on the SQL Server engine.
--
-- Mirrors the PostgreSQL outbox (migrations 0001 and 0002 there) column for column, with the
-- engine's own types. The two schemas are twins, not shared: ADR 0004 keeps each adapter's DDL
-- in its own directory with its own runner and its own tracking table.
--
-- NO TRIGGER ANYWHERE IN THIS FILE, and that is a decision rather than an omission. PostgreSQL
-- migration 0005 puts an AFTER INSERT trigger on its outbox that fires pg_notify, which gives
-- its processor a sub-second wake. SQL Server has no LISTEN/NOTIFY, and ADR 0045's engine mapping, which leaves engine-native triggers unused, scopes the
-- SQL Server trigger mechanism to polling in v1. The processor's idle poll is the whole wake
-- path here, so the trigger has nothing to signal and does not exist.
--
-- The events table's no-trigger rule (0001) also still stands: the append reads its position
-- back with OUTPUT, and OUTPUT without INTO fails on any triggered table.

CREATE TABLE event_store.outbox (
    outbox_id       BIGINT           IDENTITY(1,1) NOT NULL,
    event_id        UNIQUEIDENTIFIER NOT NULL,
    event_type      VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    payload         VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    metadata        VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    destination     VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    occurred_utc    DATETIMEOFFSET   NOT NULL,
    sent_utc        DATETIMEOFFSET   NULL,
    attempt_count   INT              NOT NULL CONSTRAINT df_outbox_attempt_count DEFAULT 0,
    next_attempt_at DATETIMEOFFSET   NULL,
    last_error      VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    global_position BIGINT           NOT NULL,
    correlation_id  AS CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(metadata, '$.correlation_id')) PERSISTED,
    CONSTRAINT pk_outbox          PRIMARY KEY CLUSTERED (outbox_id),
    CONSTRAINT uq_outbox_event_id UNIQUE NONCLUSTERED (event_id)
);
GO

-- The outbox row is fully self-describing: it copies event_type, payload, metadata,
-- occurred_utc, and global_position from the events row at append time, inside the same
-- transaction, so the processor never joins back into event_store.events to assemble a dispatch.
-- global_position is NOT NULL and lands here because AppendAsync threads it out of the events
-- INSERT's OUTPUT clause. destination is nullable for the v1 single-bus path.

-- FILTERED index on outbox_id, the counterpart of PostgreSQL's partial index. The processor
-- scans pending rows in FIFO order without indexing a column that is always NULL inside the
-- filter.
CREATE NONCLUSTERED INDEX ix_outbox_pending
    ON event_store.outbox (outbox_id)
    WHERE sent_utc IS NULL;
GO

CREATE NONCLUSTERED INDEX ix_outbox_correlation
    ON event_store.outbox (correlation_id);
GO

-- Outbox quarantine: the poison-message terminus. A separate table so the live outbox stays
-- small and its filtered index stays cheap. No foreign key to event_store.outbox, because the
-- live row is deleted on the move and an FK would block pruning; outbox_id is kept as a
-- historical reference only.
--
-- No unique constraint on event_id, matching the PostgreSQL table on disk. The PostgreSQL
-- processor's comment claims one exists and it does not; the schema is what this mirrors.
CREATE TABLE event_store.outbox_quarantine (
    quarantine_id   BIGINT           IDENTITY(1,1) NOT NULL,
    outbox_id       BIGINT           NOT NULL,
    event_id        UNIQUEIDENTIFIER NOT NULL,
    event_type      VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    payload         VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    metadata        VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    occurred_utc    DATETIMEOFFSET   NOT NULL,
    attempt_count   INT              NOT NULL,
    final_error     VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    quarantined_at  DATETIMEOFFSET   NOT NULL
        CONSTRAINT df_outbox_quarantine_quarantined_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT pk_outbox_quarantine PRIMARY KEY CLUSTERED (quarantine_id)
);
GO
