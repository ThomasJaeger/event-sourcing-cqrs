# 0054. Schema Divergences From the Book's Figures

## Status

Accepted (August 2026)

## Context

This reference implementation ships alongside a manuscript whose Chapter 8
figures declare the event store and outbox schemas. Migration 0001 departs from
those figures at several points, and the departures were catalogued during the
PostgreSQL adapter work as a numbered list of ten. The migration's header comment
cites that list, and the comment above the outbox partial index cites one entry
of it by number.

The catalogue lived in a session log. Session logs are this project's internal
process record and are not published with the implementation, so a reader of the
shipped repository meets two comments pointing at a document they do not have.

Those two comments cannot be repointed. An applied migration is immutable, its
comments included: the runner checksums each migration over its raw bytes and
refuses startup when a stored checksum disagrees, so editing a comment breaks
every database that already ran the file. The correction therefore lands here,
and names the comments it corrects rather than changing them.

A catalogue of deliberate departures, each with a reason, is a decision record,
and the numbering is preserved from the original list so that a comment naming a
divergence by number keeps its meaning.

## Decision

Record the catalogue here. Nine entries describe the current tree.

1. Events index. Figure 8.2 declares a named index on the stream and version
   pair alongside a UNIQUE constraint on the same pair. The migration omits the
   named index, because the constraint already backs one.

2. Payload and metadata column type. Figure 8.2 shows a binary large-object
   type. The migration uses JSONB, which matches the manuscript's own prose in
   its event metadata section.

3. Global position naming. The manuscript alternates between two spellings for
   the same value across Chapters 8, 13, 14, 16 and Part 4. The implementation
   commits to GlobalPosition on the envelope and global_position in the schema,
   and uses no other spelling anywhere.

4. Retired. See below.

5. Investigation query ordering. Chapter 17's investigation SQL orders by the
   occurrence timestamp. Every read path here orders by global_position, which
   is monotonic and never tied.

6. Outbox event type column. Figure 8.4 omits an event type column. The
   migration carries event_type, which the manuscript's own outbox processor
   pseudocode logs.

7. Outbox timestamp name. Figure 8.4 names the timestamp column for its creation
   time. The migration names it occurred_utc, matching the events table and
   Chapter 17's outbox depth metric.

8. Outbox backoff columns. Figure 8.4 carries no error or retry column. The
   migration carries last_error and next_attempt_at, both of which the
   surrounding prose commits to.

9. Outbox partial index. Figure 8.4 indexes the sent timestamp under a filter
   requiring that timestamp to be null, so the indexed column is null for every
   row the index covers. The migration indexes outbox_id under the same filter,
   which orders pending rows in insertion sequence.

10. Migration runners. Chapter 8 declares schemas without describing how they
    are applied. This implementation carries hand-written SQL files and an
    Npgsql-based runner in the Migrations.Postgres project.

Divergence 4 is retired. It recorded that Chapter 8 names Marten as a peer
alongside the shipped adapters while the implementation shipped three of them.
Four ship: PostgreSQL, SQL Server, KurrentDB and DynamoDB. The manuscript
question it raised stands and is manuscript work. The count it rested on is
false against this tree, so the entry is retired rather than corrected in place,
and its number is not reused.

## The comments this ADR corrects

Two comments in migrations/0001_initial_event_store.sql cite the unpublished
session log and stay as they are, because the file is immutable once applied.

The header comment names the divergences as a numbered set of flags and cites
the session log for them. Read it as citing this ADR's catalogue, entries one
through ten.

The comment above the outbox pending index cites flag nine for the choice of
indexed column. That is divergence 9 here.

No adapter comment carries this correction, and that is a reading of the adapter
rather than an omission. The PostgreSQL event store and its outbox processor
comment the statements they build and the concurrency they hold: neither file
names a figure or a departure from one, and no comment anywhere under src names
the outbox pending index as its subject. The nearest candidates explain which
columns an INSERT omits because they default, and why the drain selects under
FOR UPDATE SKIP LOCKED, which are statement and concurrency subjects rather than
schema-shape ones. A citation placed there would introduce the subject instead of
joining an existing explanation, so this ADR is the sole correction.

## Consequences

- A reader who follows either migration comment reaches a document they do not
  have. This ADR is where that reader is served, and it is published with the
  implementation.
- The catalogue is subject to the citation checker and to whatever
  reconciliation governs the ADR set, which a session log was not.
- Entries 3, 5 and 10 describe differences whose remedy is manuscript work. They
  are recorded here as facts about the difference and commit this repository to
  nothing.

## Trigger for revisiting

A change to migration 0001 that closes or widens a divergence, which under the
immutability rule means a new migration rather than an edit. A manuscript
revision that removes one. A second engine's initial migration diverging from
the same figures, which would raise whether the catalogue is per engine or
shared.
