-- migrations/sqlserver/0006_add_snapshots.sql
--
-- Phase 15: the snapshot store table (Chapter 12), the SQL Server twin of PostgreSQL
-- migrations/0023_add_snapshots.sql. One row per aggregate stream, upserted on capture, so a load is
-- a single primary-key lookup. A snapshot is a performance optimization over full replay and never a
-- source of truth: snapshot_schema_version lets a reader discard a row whose shape no longer matches
-- and rebuild from events, and stream_version is the point a load replays the tail from.
--
-- Per ADR 0004 this directory is self-contained: it numbers from 0001 and shares nothing with the
-- PostgreSQL set, so this is 0006 here to that set's 0023. payload carries the same explicit
-- column-level UTF-8 collation the events table uses, because the database default collation is not
-- UTF-8; the store binds it as NVARCHAR so non-ASCII survives the trip into the column.

CREATE TABLE event_store.snapshots (
    stream_id               VARCHAR(200)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    stream_version          INT            NOT NULL,
    snapshot_schema_version SMALLINT       NOT NULL,
    payload                 VARCHAR(MAX)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    captured_utc            DATETIMEOFFSET NOT NULL,
    CONSTRAINT pk_snapshots PRIMARY KEY CLUSTERED (stream_id)
);
GO
