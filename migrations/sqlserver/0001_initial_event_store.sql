-- migrations/sqlserver/0001_initial_event_store.sql
-- Chapter 8: Event Store schema, the SQL Server engine.
--
-- Engine floor: SQL Server 2019. UTF-8 collations arrived in 2019, and the payload and
-- metadata columns depend on one. 2022 is the primary target; 2019 is what CI proves.
--
-- This directory starts its own NNNN sequence at 0001. It is a separate database on a
-- separate engine, so it shares no numbering, no tracking table, and no runner with the
-- PostgreSQL migrations one directory up (ADR 0004: adapters are self-contained).
--
-- NO TRIGGERS ON event_store.events, EVER. The append reads its assigned global_position
-- back with INSERT ... OUTPUT INSERTED.global_position, and OUTPUT without INTO fails on a
-- table that has any trigger. The no-trigger rule and the append mechanism are one decision.
--
-- Batches are separated by GO. GO is a client-side separator, not T-SQL: SqlCommand does not
-- understand it, so SqlServerMigrationRunner splits on it. CREATE SCHEMA has to be alone in
-- its batch, which is what forced the split in the first place.

CREATE SCHEMA event_store;
GO

-- Events: append-only, ordered by global_position for replay and projections.
--
-- global_position is the CLUSTERED primary key. The log is read in position order, so the
-- physical order and the read order are the same and the common scan is sequential.
--
-- correlation_id, causation_id, and tenant_id are PERSISTED computed columns over the
-- metadata JSON, the counterpart of the PostgreSQL STORED generated columns in migrations
-- 0001 and 0016. They let the AdminConsole tracer and the tenant filter index without
-- parsing JSON at query time.
--
-- The tenant default Guid is a frozen literal matching WellKnownTenants.Default (ADR 0029).
-- A computed-column expression cannot reference a constant, and the value must never drift,
-- so it is pinned here in a checksummed migration exactly as PostgreSQL migration 0016 pins
-- it. JSON_VALUE returns SQL NULL both for an absent property and for an explicit JSON null,
-- so COALESCE covers a pre-tenancy row either way.
--
-- payload and metadata are VARCHAR(MAX) with an explicit column-level UTF-8 collation. The
-- database default collation is not UTF-8, so the collation has to be declared per column.
CREATE TABLE event_store.events (
    global_position BIGINT           IDENTITY(1,1) NOT NULL,
    stream_id       VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    stream_version  INT              NOT NULL,
    event_id        UNIQUEIDENTIFIER NOT NULL,
    event_type      VARCHAR(200)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    event_version   SMALLINT         NOT NULL,
    payload         VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    metadata        VARCHAR(MAX)     COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    occurred_utc    DATETIMEOFFSET   NOT NULL,
    correlation_id  AS CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(metadata, '$.correlation_id')) PERSISTED,
    causation_id    AS CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(metadata, '$.causation_id'))   PERSISTED,
    tenant_id       AS CONVERT(UNIQUEIDENTIFIER, COALESCE(JSON_VALUE(metadata, '$.tenant_id'),
                        '00000000-0000-0000-0000-000000000001')) PERSISTED,
    CONSTRAINT pk_events                PRIMARY KEY CLUSTERED (global_position),
    CONSTRAINT uq_events_stream_version UNIQUE NONCLUSTERED (stream_id, stream_version),
    CONSTRAINT uq_events_event_id       UNIQUE NONCLUSTERED (event_id)
);
GO

-- Both uniqueness rules are named CONSTRAINTs, never bare unique indexes. SQL Server raises
-- a different error number for each mechanism: 2627 for a constraint, 2601 for an index. The
-- adapter's concurrency translation filters on the constraint name, so committing to
-- constraints keeps one error number on the append path and keeps the name in the message.

-- Index parity with the PostgreSQL schema: correlation_id is indexed (migration 0001) and
-- tenant_id is indexed (migration 0016). causation_id is a computed column with no index
-- there, so it gets none here. Causation tracing walks streams; correlation tracing queries
-- the log directly.
CREATE NONCLUSTERED INDEX ix_events_correlation_id
    ON event_store.events (correlation_id);
GO

CREATE NONCLUSTERED INDEX ix_events_tenant_id
    ON event_store.events (tenant_id);
GO

-- Schema migrations: SqlServerMigrationRunner reads version and checksum on each run to
-- decide what to apply and to detect a file edited after it was applied.
CREATE TABLE event_store.schema_migrations (
    version     INT            NOT NULL,
    name        VARCHAR(200)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    applied_at  DATETIMEOFFSET NOT NULL CONSTRAINT df_schema_migrations_applied_at DEFAULT SYSUTCDATETIME(),
    checksum    VARCHAR(64)    COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    CONSTRAINT pk_schema_migrations PRIMARY KEY CLUSTERED (version)
);
GO
