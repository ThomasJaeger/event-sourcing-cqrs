-- Test-only migration, not part of the shipped schema. Embedded in the test assembly
-- and reached through MigrationRunner's (assembly, resourcePrefix) constructor seam.
--
-- 0001 carries the tracking table because the runner probes for
-- event_store.schema_migrations before every read and inserts into it after every apply.
-- The columns match migrations/0001_initial_event_store.sql, which is what the runner
-- reads and writes.

CREATE SCHEMA event_store;

CREATE TABLE event_store.schema_migrations (
    version     INT          NOT NULL,
    name        TEXT         NOT NULL,
    applied_at  TIMESTAMPTZ  NOT NULL DEFAULT now(),
    checksum    TEXT         NOT NULL,
    CONSTRAINT pk_schema_migrations PRIMARY KEY (version)
);

CREATE TABLE event_store.ordering_alpha (id INT NOT NULL);
