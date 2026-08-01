-- Test-only migration, not part of the shipped schema.
--
-- The top of the applied baseline. Together with 0001 it forms the Sparse set, so a database
-- that has applied that set reports a highest applied version of 3, which is what the
-- BackFilled and Forward runs are measured against.

CREATE TABLE event_store.ordering_gamma (id INT NOT NULL);
