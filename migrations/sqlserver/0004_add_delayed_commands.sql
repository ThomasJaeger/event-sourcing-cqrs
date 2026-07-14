-- migrations/sqlserver/0004_add_delayed_commands.sql
--
-- Delay queue for scheduled commands (ADR 0017), the SQL Server twin of PostgreSQL migrations
-- 0008 and 0020 folded into one file, so tenant_id is born on both tables.
--
-- A process manager schedules a timeout command to fire at fire_at_utc;
-- SqlServerDelayQueueProcessor claims due rows with WITH (UPDLOCK, READPAST, ROWLOCK), the T-SQL
-- counterpart of FOR UPDATE SKIP LOCKED, and dispatches them through ICausedCommandBus. The row
-- lock is the claim: there is no claimed_at column, and a claimant that dies without committing
-- releases its rows when the engine releases its locks.
--
-- The dispatch-metadata columns (correlation_id through idempotency_key) are exactly the values
-- CausedCommandBus needs to rebuild the command context, so the row is self-describing and the
-- processor never joins back to anything.
--
-- NO TRIGGER, and the absence is the design. PostgreSQL migration 0008 puts an AFTER INSERT
-- trigger here that fires pg_notify to shorten latency for at-or-near-present rows. SQL Server has
-- no LISTEN/NOTIFY and PLAN.md:245 scopes this engine to polling in v1, so the processor's idle
-- poll carries the whole duty and there is nothing for a trigger to signal.

CREATE TABLE event_store.delayed_commands (
    delayed_command_id     BIGINT           IDENTITY(1,1) NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    fire_at_utc            DATETIMEOFFSET   NOT NULL,
    command_type           VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    command_payload        VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    correlation_id         UNIQUEIDENTIFIER NOT NULL,
    causation_id           UNIQUEIDENTIFIER NOT NULL,
    actor_id               UNIQUEIDENTIFIER NOT NULL,
    service_name           VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    idempotency_key        VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    scheduled_by_stream_id VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    scheduled_by_step      VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    scheduled_utc          DATETIMEOFFSET   NOT NULL
        CONSTRAINT df_delayed_commands_scheduled_utc DEFAULT SYSUTCDATETIME(),
    dispatched_at_utc      DATETIMEOFFSET   NULL,
    cancelled_at_utc       DATETIMEOFFSET   NULL,
    cancellation_reason    VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    attempt_count          INT              NOT NULL
        CONSTRAINT df_delayed_commands_attempt_count DEFAULT 0,
    last_error             VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    next_attempt_at        DATETIMEOFFSET   NULL,
    CONSTRAINT pk_delayed_commands PRIMARY KEY CLUSTERED (delayed_command_id)
);
GO

-- Due-row scan: FILTERED on the active set, the counterpart of PostgreSQL's partial index, so the
-- index stays proportional to pending work rather than to total history.
CREATE NONCLUSTERED INDEX ix_delayed_commands_due
    ON event_store.delayed_commands (fire_at_utc)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;
GO

-- Cancellation lookup: process managers cancel by (stream, step); filtered for the same reason.
CREATE NONCLUSTERED INDEX ix_delayed_commands_by_stream
    ON event_store.delayed_commands (scheduled_by_stream_id, scheduled_by_step)
    WHERE dispatched_at_utc IS NULL AND cancelled_at_utc IS NULL;
GO

-- Quarantine: terminal store for rows whose dispatch exhausted its retries, reached by an atomic
-- DELETE ... OUTPUT ... INTO in the processor, mirroring outbox_quarantine. No extra index:
-- queries here are operational, not hot.
CREATE TABLE event_store.delayed_commands_quarantine (
    quarantine_id          BIGINT           IDENTITY(1,1) NOT NULL,
    delayed_command_id     BIGINT           NOT NULL,
    tenant_id              UNIQUEIDENTIFIER NOT NULL,
    fire_at_utc            DATETIMEOFFSET   NOT NULL,
    command_type           VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    command_payload        VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    correlation_id         UNIQUEIDENTIFIER NOT NULL,
    causation_id           UNIQUEIDENTIFIER NOT NULL,
    actor_id               UNIQUEIDENTIFIER NOT NULL,
    service_name           VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    idempotency_key        VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    scheduled_by_stream_id VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    scheduled_by_step      VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    attempt_count          INT              NOT NULL,
    final_error            VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    quarantined_at         DATETIMEOFFSET   NOT NULL
        CONSTRAINT df_delayed_commands_quarantine_quarantined_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT pk_delayed_commands_quarantine PRIMARY KEY CLUSTERED (quarantine_id)
);
GO
