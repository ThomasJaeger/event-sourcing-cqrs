# 0044. Commit-Ordered Global Position

## Status

Accepted (July 2026)

## Context

global_position is assigned by an IDENTITY column while the append transaction is
open (migrations/0001_initial_event_store.sql:14, read back mid-transaction through
RETURNING global_position in PostgresEventStore). Identity and sequence values are
handed out before commit, so under concurrent appends a committed reader could
observe position N+1 while position N belonged to a transaction still open. Nothing
on disk serialized assignment against commit.

Every checkpointing reader assumed commit-ordered visibility without a
specification saying so. ReadAllAsync serves catch-up from a checkpoint with a
strict greater-than. PostgresCheckpointStore advances with GREATEST, so the mark
never retreats. The eight projection handlers skip any event at or below their
checkpoint. A reader that observed N+1 and checkpointed past a still-uncommitted N
therefore skipped N permanently once it committed. The outbox drain is claim-based
and immune on its own, and its redelivery of the late-committing event was rejected
by the same checkpoint guard, so the loss was silent and permanent on the live tail
and during catch-up alike. The defect was found by the SQL Server adapter
pre-flight, which asked whether the PostgreSQL design tolerates commit-order gaps
before a second adapter copied the answer.

Concurrent writers exist by construction: the Api serves commands concurrently, the
Workers host drains the outbox into process managers that issue commands, and the
delay-queue processor dispatches timeouts.

## Decision

The event store provides commit-ordered visibility of the global position, held as
an invariant by every adapter:

No transaction may hold an assigned-but-uncommitted global position while a
transaction holding a higher position commits. Event rows become visible in
global-position order. Any position gap a committed reader observes is permanent (a
rolled-back append), never transient.

The PostgreSQL adapter holds the invariant with a transaction-scoped exclusive
advisory lock, pg_advisory_xact_lock on PostgresEventStore.AppendAdvisoryLockKey,
acquired as the first statement inside the append transaction on both append paths,
before any position-drawing INSERT, and released implicitly at commit or rollback.
Commit 24932e1 lands the repair together with the two-fact behavioral test that
pins it (PostgresEventStore_CommitVisibility_Tests, one fact per append path).

Every future adapter holds the same invariant through its engine's counterpart. On
SQL Server that is sp_getapplock at transaction scope, since IDENTITY and SEQUENCE
values are handed out before commit there as well. The contract suite, once
extracted, pins the invariant with a concurrent-append visibility probe, so a green
bar means the property holds rather than that the question was never asked.

## Consequences

- Appends serialize globally: one event-store write transaction at a time, for the
  duration of the events and outbox inserts and the commit. This is a real
  throughput ceiling, measurable and documented, accepted in exchange for retiring
  silent data loss. Single-writer position assignment is the design of
  purpose-built stores in this space.
- The checkpointing readers are correct as written. ReadAllAsync catch-up, the
  projection skip-guards, GREATEST checkpoint advancement, and high-water tracking
  over committed reads rest on a property the store provides rather than on an
  assumption.
- Gaps in the position sequence remain possible and are permanent only: a
  rolled-back append burns its positions. Readers tolerate gaps and never wait on
  one.
- Production remediation is an operator action this ADR records rather than
  performs. Events skipped before the repair are invisible to every checkpoint; the
  heal is a per-projection rebuild, since the rebuild path disables the skip-guard
  with a floor of -1. Only OrderThroughput has a rebuild surface today, which the
  residual ledger carries.
- PostgresEventStore_ReadAllAsync_Tests pins exact position contiguity, which
  over-pins this contract; SQL Server burns identity-cache positions on restart.
  That assertion relaxes to this specification when the contract suite is
  extracted, and no earlier.
- docs/TDD_RULES.md's gap-tolerance clause points at this ADR as its specification.

## Rejected alternatives

**A visibility-watermark read side.** Readers advance only to the lowest position
guaranteed committed, detecting gaps with grace timeouts. Substantial standing
machinery, it needs a way to distinguish transient from permanent gaps that
PostgreSQL does not surface cheaply, and it repairs the catch-up readers while
leaving the live-tail guard drop to a second, separate fix.

**A processed-event-set checkpoint guard.** Replacing the high-water mark with a
per-event processed set touches all eight projections, converts a constant-time
guard into per-event lookups, and leaves ReadAllAsync catch-up lossy.

**Documenting the hazard and requiring gap-tolerant readers.** No reader-side
discipline recovers an event the checkpoint has passed, so this documents data
loss rather than preventing it.

**A gapless counter table with SELECT FOR UPDATE.** The same serialization cost as
the advisory lock, plus an extra table, an extra write per append, and a hotter
row.

## Trigger for revisiting

A measured throughput need the serialized append path cannot meet reopens the
design toward a visibility-watermark read side, weighed then against the machinery
cost this ADR rejected at current scale.

## Amendment (July 2026)

The SQL Server adapter holds the invariant with sp_getapplock, exclusive, at
transaction scope, as the first statement of both append transactions. On this
engine the hazard is configuration-dependent: latent under default lock-based
READ COMMITTED, where a tailing reader blocks on the writer's lock, and active
under READ_COMMITTED_SNAPSHOT, where the reader skips the uncommitted row
exactly as PostgreSQL's MVCC does. The test fixtures enable RCSI on every SQL
Server test database, so the commit-visibility probe exercises the skip hazard
rather than the blocking behavior. ADR 0045 carries the full engine mapping.

Three forward references above are discharged: the contract suite landed at
6baaa2f and pins the invariant with its concurrent-append visibility probe; the
exact-contiguity assertion in PostgresEventStore_ReadAllAsync_Tests, named in the
consequences, relaxed to this specification at 7b57366; and
PostgresEventStore_CommitVisibility_Tests, named in the decision, was retired
into the suite in that same commit.
