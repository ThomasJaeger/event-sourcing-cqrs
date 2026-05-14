-- migrations/0003_initial_read_models.sql
-- Session 0005: read-model schema foundation and projection checkpoints.
-- Read models share the event store's PostgreSQL database in v1. The
-- event_store schema (migrations 0001-0002) holds events and the outbox; the
-- read_models schema holds read-model tables and projection checkpoints. A
-- single transaction can span both schemas, which is how a projection handler
-- advances its checkpoint atomically with the read-model write it just made.
-- The read-model tables themselves arrive in later migrations, each shipped
-- alongside the projection that owns it.

CREATE SCHEMA read_models;

-- One row per projection. position is the global_position of the last event
-- the projection processed; the projection resumes by passing position as the
-- exclusive fromPosition to IEventStore.ReadAllAsync. AdvanceAsync UPSERTs with
-- GREATEST so an at-least-once redelivery carrying an already-processed
-- position cannot move the checkpoint backwards.
CREATE TABLE read_models.projection_checkpoints (
    projection_name  TEXT    NOT NULL,
    position         BIGINT  NOT NULL,
    CONSTRAINT pk_projection_checkpoints PRIMARY KEY (projection_name)
);
