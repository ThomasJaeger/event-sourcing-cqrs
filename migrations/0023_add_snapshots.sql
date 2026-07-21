-- migrations/0023_add_snapshots.sql
-- Phase 15: the snapshot store table (Chapter 12). One row per aggregate stream, upserted on capture,
-- so a load is a single primary-key lookup. A snapshot is a performance optimization over full replay
-- and never a source of truth: snapshot_schema_version lets a reader discard a row whose shape no
-- longer matches and rebuild from events, and stream_version is the point a load replays the tail from.
CREATE TABLE event_store.snapshots (
    stream_id               TEXT        NOT NULL,
    stream_version          INTEGER     NOT NULL,
    snapshot_schema_version SMALLINT    NOT NULL,
    payload                 JSONB       NOT NULL,
    captured_utc            TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_snapshots PRIMARY KEY (stream_id)
);
