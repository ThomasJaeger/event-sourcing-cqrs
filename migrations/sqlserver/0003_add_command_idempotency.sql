-- migrations/sqlserver/0003_add_command_idempotency.sql
--
-- Command idempotency store for the IdempotencyBehavior pipeline behavior (ADR 0016), the SQL
-- Server twin of PostgreSQL migrations 0007 and 0019 folded into one file: this directory starts
-- fresh at 0001, so the tenant column is born on the table rather than bolted on later.
--
-- A command dispatched with an idempotency key records the key here after its handler succeeds;
-- a later dispatch with the same key is a duplicate and short-circuits. The primary key is what
-- the eager-check-with-lazy-fallback pattern leans on: a concurrent second insert of the same key
-- loses the race against it, and the store reads that as a duplicate.
--
-- The key is (tenant_id, idempotency_key). The same key under two tenants is two different
-- commands and must not collide.

CREATE TABLE event_store.command_idempotency (
    tenant_id       UNIQUEIDENTIFIER NOT NULL,
    idempotency_key VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    command_type    VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    processed_utc   DATETIMEOFFSET   NOT NULL
        CONSTRAINT df_command_idempotency_processed_utc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT pk_command_idempotency PRIMARY KEY CLUSTERED (tenant_id, idempotency_key)
);
GO

-- processed_utc index supports time-based retention (pruning or partitioning) later; retention
-- itself is deferred (ADR 0016).
CREATE NONCLUSTERED INDEX ix_command_idempotency_processed_utc
    ON event_store.command_idempotency (processed_utc);
GO
