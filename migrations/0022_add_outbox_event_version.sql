-- migrations/0022_add_outbox_event_version.sql
-- Phase 15: carry the event's schema version on the outbox row and its quarantine, so the
-- dispatch path can upcast a stored event to the current shape on read the way the store's own
-- reads do. Existing rows default to 1, which is true by construction: every event written before
-- this migration is schema version 1.
ALTER TABLE event_store.outbox
    ADD COLUMN event_version SMALLINT NOT NULL DEFAULT 1;

ALTER TABLE event_store.outbox_quarantine
    ADD COLUMN event_version SMALLINT NOT NULL DEFAULT 1;
