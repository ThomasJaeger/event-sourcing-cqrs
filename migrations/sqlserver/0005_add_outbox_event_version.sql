-- migrations/sqlserver/0005_add_outbox_event_version.sql
-- Phase 15: the SQL Server twin of migrations/0022. Carry the event's schema version on the outbox
-- row and its quarantine so the dispatch path can upcast on read the way the store's own reads do.
-- Existing rows default to 1, true by construction. The default constraints are named per the
-- table's convention, and like df_outbox_quarantine_quarantined_at they are default constraints, so
-- the migration runner's key-constraint checks do not see them.
ALTER TABLE event_store.outbox
    ADD event_version SMALLINT NOT NULL
        CONSTRAINT df_outbox_event_version DEFAULT 1;

ALTER TABLE event_store.outbox_quarantine
    ADD event_version SMALLINT NOT NULL
        CONSTRAINT df_outbox_quarantine_event_version DEFAULT 1;
