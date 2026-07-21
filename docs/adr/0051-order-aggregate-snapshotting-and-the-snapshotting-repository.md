# 0051. Order Aggregate Snapshotting and the Snapshotting Repository

## Status

Accepted (July 2026)

## Context

Phase 15's second arc is snapshots, the Chapter 12 pattern: a long event stream is
expensive to replay on every load, so the repository periodically captures the
aggregate's rehydrated state as a memento and, on a later load, restores the memento
and replays only the events after it. This ADR records the decisions the arc made,
from the aggregate's snapshot seam through the store, the snapshotting repository, the
four-engine composition, and the end-to-end facts that pin equivalence and the speedup.
The pattern is real and running: the Order aggregate snapshots every fifty events on
all four engines, and a load rehydrates from the latest memento plus the tail after it.
It is the sibling of ADR 0050's versioning arc, and the two share Phase 15.

## Decisions

**The aggregate owns its memento, and the restore seam is guarded to a pristine
instance.** `OrderSnapshot` is a domain record the `Order` aggregate produces through
`ToSnapshot()` and restores through `RestoreFrom(snapshot, version)`; the aggregate
implements `ISnapshotSource<TSnapshot>`, so the capture and the restore are the
aggregate's own, not the repository's. `RestoreFrom` calls `AggregateRoot.RestoreVersion`
first, which throws if the aggregate is not pristine, before it seats any state.
Rejected: the repository building the memento by reading the aggregate's fields, which
reaches into private state the aggregate hides and couples the snapshot to a shape that
refactoring should be free to change; the aggregate is the only thing that knows how to
capture and restore itself. Rejected: an unguarded restore that seats a version onto any
instance, because teleporting a version onto an aggregate that has already applied events
or holds uncommitted work overwrites the concurrency token the next append checks its
expected version against, and a stale write then lands as if it were current. The guard
turns that into a loud failure on a non-pristine restore.

**`ISnapshotStore` is generic per call, and the schema filter lives in the store's
query.** `SaveAsync<TSnapshot>` and `LoadAsync<TSnapshot>` are generic methods on a
non-generic interface, because one store serves every aggregate's memento type and a
generic-per-aggregate interface would multiply registrations for a single table. `LoadAsync`
takes the schema version the caller can consume and filters it in the WHERE clause, so a
row at a shape the caller cannot read never crosses the wire as a payload to deserialize;
it reads as a miss, indistinguishable from an absent stream. Rejected: `ISnapshotStore<TSnapshot>`
closed per aggregate, which is a registration and a table per snapshot type for a store
whose SQL is identical across types. Rejected: reading the row and comparing the schema
version in memory, which deserializes a payload the caller is about to discard, and a
shape a mismatched reader may not be able to deserialize at all.

**One upserted row per stream.** A capture upserts the stream's single snapshot row, so
the newest memento replaces the older one and a load is a single primary-key lookup. The
upsert is `INSERT ... ON CONFLICT (stream_id) DO UPDATE` on PostgreSQL and, because T-SQL
has no such form, an `UPDATE` under `(UPDLOCK, HOLDLOCK)` then a conditional `INSERT` inside
one transaction on SQL Server, the concurrency-safe idiom `SqlServerIdempotencyStore`
already commits to. Rejected: an append-only snapshot history keyed by version, which
makes a load scan and sort for the latest row and grows without bound, for a cache that
only ever needs its newest entry.

**Every engine registers a snapshot store, each on its own precedent, and the Order
override is unconditional.** The relational engines register a native store on the
event-store database beside the idempotency store; the non-relational engines register the
PostgreSQL store on the read-model companion, the ADR 0046 posture that the idempotency
store and delay queue already take on those engines.

| Engine | Snapshot store | Database |
|---|---|---|
| PostgreSQL | PostgresSnapshotStore | event-store database |
| SQL Server | SqlServerSnapshotStore | event-store database |
| KurrentDB | PostgresSnapshotStore | read-model companion |
| DynamoDB | PostgresSnapshotStore | read-model companion |

The SQL Server native store shipped first, one commit ahead of the composition, so the
override could be written without a capability check. `AddApplication` registers the Order
repository as a closed-generic override that resolves `ISnapshotStore` as a required
dependency. Rejected: a null-tolerant composition that resolves the snapshotting repository
when a store is present and the plain repository when it is absent, because a host that
forgot to register the store would degrade in silence to full replay, the same silent hole
ADR 0050 refused for a null-tolerant upcaster pipeline. A required dependency turns a
misconfigured host into a resolution failure at composition rather than a slow read in
production. Rejected: binding the PostgreSQL store to the read-model companion on the SQL
Server arm too, which would put the one write-side relational store not native to that
engine on a Postgres companion the arm otherwise has no reason to compose; the native store
keeps the arm's relational stores on one engine (ADR 0004).

**The interval trigger is boundary math on the version, not an event counter.** A save
captures a snapshot when the append moves the stream into a new interval bucket, expressed
as `postVersion / interval > preVersion / interval` on integer division, so a multi-event
append that leaps past a boundary captures once, at the post-append version. Rejected:
capturing when `postVersion % interval == 0`, because a multi-event append that steps over
the exact multiple lands past it and never captures. Rejected: a running count of events
since the last snapshot, which is per-stream state the repository would have to load and
persist to read, when the version the aggregate already carries answers the same question.

**Capture is best-effort, and the append is never inside the wrap.** The append commits
first, outside the try; the capture runs after it and any snapshot failure is logged at
warning naming the stream and swallowed, so a snapshot store that is down costs a warning
rather than the durable write the command already made. Cancellation is not a snapshot
failure and is excepted from the swallow, so a cancelled command propagates rather than
continuing in silence. Rejected: wrapping the append and the capture in one guard, which
lets a snapshot-store outage fail a write that has nothing to do with snapshots. Rejected:
swallowing cancellation with the rest, which violates the no-swallowed-cancellation rule.

**The speedup is pinned as a replay count, not a wall clock.** The end-to-end fact seeds a
stream past two boundaries, loads through the snapshotting repository over a store that
records the read position, and asserts the read began at the snapshot's version and
replayed strictly fewer events than the full stream. Rejected: asserting the snapshot load
is faster than a full replay by a wall-clock margin, because a timing budget starves on a
shared 4-core CI runner, the scar ADR 0049 records for the DynamoDB append-retry cap: the
same code that survived a 32-core box five runs out of five starved on a 4-core runner, the
scheduler widening the window. A replay count is what the read saves and is
machine-independent.

**A snapshot shape change is a discard-and-rebuild, never an upcast.** `snapshot_schema_version`
records the memento's shape; a load asks at the version the repository is composed for, and
a stored snapshot at any other version reads as a miss, so the aggregate rebuilds from
events and the next boundary crossing captures a fresh snapshot at the current version.
Rejected: upcasting old snapshots through a chain the way events are upcast, because a
snapshot is a cache the aggregate can always rebuild from history, so the upcaster machinery
buys nothing a discard does not, and a snapshot lineage is a second set of upcasters to keep
correct beside the event ones.

**The process-manager twin is absent by scope.** `ProcessManagerRepository` has no
snapshotting variant, because no process manager in v1 has a stream long enough to earn one.
It is named here as the twin the first long PM stream would force, the same way ADR 0050
named the PM write-stamp as the twin the first PM upcaster forces.

## Consequences

- The Order aggregate snapshots on all four engines, and a load rehydrates from the latest
  memento plus the tail after it, pinned end to end against the real PostgreSQL store:
  snapshot-plus-tail equals a full replay, the read begins at the snapshot's version, and a
  schema-mismatched snapshot discards and rebuilds at the current version.
- The Order override is a required dependency, so a host that composes it without a provider
  arm supplying `ISnapshotStore` fails at resolution rather than degrading to a silent full
  replay.
- A snapshot shape change discards and rebuilds, so there is no snapshot upcaster lineage to
  keep correct beside the event upcasters.
- The snapshotting repository's write path is duplicated from `EventStoreRepository` rather
  than shared, because that class is sealed with private helpers and sharing would widen its
  surface. The duplication is flagged for a later slice that extracts a shared envelope
  factory both repositories call.
- The plan's snapshot-storage wording, "separate PostgreSQL table" (PLAN.md:488), under-specifies
  the shipped four-engine posture: snapshots live in `event_store.snapshots` on the relational
  engines' event-store database and on the read-model companion for the non-relational engines,
  not a single standalone PostgreSQL table. Per the source-of-truth hierarchy the code is
  canonical; the wording is flagged for the cross-track ledger.

## Trigger for revisiting

A second aggregate earning a memento reopens the per-root override. One override registers
by hand today; a second is the point at which a convention, an open-generic snapshotting
registration keyed on the aggregates that implement `ISnapshotSource`, earns its abstraction
over hand-registering each root, the same way ADR 0050 defers an `IUpcasterProvider` until a
second bounded context registers a batch.

A snapshot shape that changes often enough that rebuilding long streams from events becomes
the dominant cost reopens discard-and-rebuild. Discard is the cheaper honesty while a memento
shape is stable; a snapshot that churns under schema pressure is the point at which a snapshot
upcaster earns its second lineage.

An engine that offers a native snapshot facility reopens the four-engine posture, the way ADR
0046's native-scheduler trigger reopens the companion port. Reusing the PostgreSQL store on the
non-relational engines is the cheaper honesty while no engine ships a native one; an
engine-native snapshot store would be its own implementation and its own ADR.
