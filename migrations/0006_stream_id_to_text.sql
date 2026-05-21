-- 0006_stream_id_to_text.sql
--
-- Changes events.stream_id from UUID to TEXT to support the typed StreamId
-- format introduced by ADR 0011 ({prefix}:{guid:N}, e.g., 'order:8d4f...').
--
-- This migration assumes the standard fresh-DB workflow: tests run against a
-- newly-created database with no rows, and developers using the reference
-- implementation locally recreate the DB between sessions. An upgraded dev DB
-- with rows accumulated under migrations 0001-0005 cannot be auto-converted:
-- those rows hold raw UUIDs that the new format rejects on read. The guard
-- below fails loudly in that case; recreate the database to apply.

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM event_store.events LIMIT 1) THEN
        RAISE EXCEPTION 'Migration 0006 requires an empty events table. '
                        'The stream_id format changes from UUID to '
                        '{prefix}:{guid:N} and existing rows cannot be '
                        'auto-converted. Recreate the database to apply.';
    END IF;
END $$;

ALTER TABLE event_store.events
    ALTER COLUMN stream_id TYPE TEXT
    USING stream_id::TEXT;
