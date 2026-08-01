-- Test-only migration, not part of the shipped schema.
--
-- The back-filled one. It belongs to the BackFilled set only, so a run of that set
-- against a database that applied the Sparse set finds 0002 pending below an applied
-- 0003. The table it creates is the evidence that nothing ran when the runner refuses.

CREATE TABLE event_store.ordering_beta (id INT NOT NULL);
