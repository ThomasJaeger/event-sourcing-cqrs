-- Test-only migration, not part of the shipped schema.
--
-- The forward one. It belongs to the Forward set only, so a run of that set against a
-- database that applied the Sparse set finds 0004 pending above an applied 0003. That
-- is the state the non-vacuity fact needs and the shipped migration set cannot reach.

CREATE TABLE event_store.ordering_delta (id INT NOT NULL);
