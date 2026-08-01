-- Test-only migration, not part of the shipped schema. Embedded in the test assembly and
-- reached through SqlServerMigrationRunner's (assembly, resourcePrefix) constructor seam.
--
-- 0001 carries the tracking table because the runner probes for
-- event_store.schema_migrations before every read and inserts into it after every apply.
-- The columns match migrations/sqlserver/0001_initial_event_store.sql, minus the UTF-8
-- column collations: the runner writes ASCII names and hex checksums here.
--
-- CREATE SCHEMA has to be alone in its batch, so this file carries the GO separators the
-- runner splits on.

CREATE SCHEMA event_store;
GO

CREATE TABLE event_store.schema_migrations (
    version     INT            NOT NULL,
    name        VARCHAR(200)   NOT NULL,
    applied_at  DATETIMEOFFSET NOT NULL CONSTRAINT df_schema_migrations_applied_at DEFAULT SYSUTCDATETIME(),
    checksum    VARCHAR(64)    NOT NULL,
    CONSTRAINT pk_schema_migrations PRIMARY KEY CLUSTERED (version)
);
GO

CREATE TABLE event_store.ordering_alpha (id INT NOT NULL);
GO
