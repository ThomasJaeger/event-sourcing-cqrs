# 0052. Migration Demo Entry Surfaces and Delivery Postures

## Status

Accepted (July 2026)

## Context

Phase 16's migration demo is the Chapter 18 teaching artifact: a CRUD-shaped legacy
order system and four patterns that carry it toward event sourcing, CDC,
outbox-on-legacy, the strangler, and shadow mode, running end to end from
`src/Migration` under one compose command. It is standalone from the reference
system's own hosts by scope (PLAN.md's Phase 16 standalone goal), and it is a teaching artifact, not a
production migration (PLAN.md's Phase 16 out-of-scope entry). This ADR records the decisions the demo made that
are not obvious from the code: where each pattern's events enter the event-sourced
side, how the two cross-database delivery paths behave, and how identity and
versioning cross the legacy boundary. The four patterns and their tests are real and
running; the residuals the demo leaves open live in the phase's close doc, not here.

## Decisions

**Translation paths append below the command pipeline; the strangler dispatches
through it.** CDC and outbox-on-legacy both translate legacy history into domain
events and append them to the store through the shared `EventStreamAppender`, below
the command bus. A translated event is a fact the legacy system already recorded, and
an aggregate invariant must not get a vote on a fact that already happened: a legacy
order that was placed is placed, whatever the domain would say about placing it now.
The strangler is the opposite case. It routes new placements, decisions being made for
the first time, and a new decision enters where the invariants live, through
`ICommandBus` and the full command pipeline: draft, add a line, set an address, place.
Rejected: routing the translation paths through the command pipeline, because the
pipeline exists to guard new decisions, and an invariant that rejected a historical
event, or an idempotency behavior that deduped one, would strand or drop a fact the
source system holds; history is replayed, not re-decided. Rejected: entering the
strangler's event-sourced side through `IEventStoreRepository<Order>` directly, the
Option B fork weighed this session. The strangler's purpose is to route live traffic
to the new application, and the application is the command pipeline the hosts dispatch
through, not the aggregate write path the handlers use inside it. A repository entry
would strangle toward a bypass of the very behavior being migrated to, and would prove
nothing about the pipeline the production system runs.

**The legacy drain is a demo-local emitter, the outbox shape's third independent
instance.** The outbox-on-legacy pattern writes the CRUD row and a serialized domain
event to a `legacy.legacy_outbox` table in one transaction, and a demo-local
`LegacyOutboxEmitter` drains it into the event store. This is the transactional-outbox
shape appearing a third time, beside the shipped PostgreSQL and SQL Server outbox
processors, and it stays a separate instance on the same reasoning ADR 0004 keeps each
engine's drain self-contained: the pattern is copied deliberately across a boundary,
not shared through an abstraction. Rejected: configuring the shipped `OutboxProcessor`
against the legacy database, because it drains `event_store.outbox` in the event-store
schema, and aiming it at a foreign table in a foreign database would couple a shipped
component to the demo's shape and to a schema it was never written for. Rejected:
extracting a shared drain abstraction over the shipped processors and the demo emitter,
because ADR 0004 reserves the cross-adapter drain abstraction for the revisit a real
third relational engine would force, and a teaching artifact is not that engine; the
demo shows the pattern by re-implementing it, which is what a reader migrating a real
system does.

**Both cross-database delivery paths are at-least-once, stated rather than hidden.**
The CDC reader advances its checkpoint only after its appends commit, and the outbox
emitter stamps `emitted_utc` only after its appends commit. In each the append store
and the legacy cursor are separate databases, so the append and the cursor advance
cannot share a transaction, and a crash between them replays the batch on the next run.
The append side does not dedupe, so a replay re-emits. This is named at-least-once in
both drivers' headers rather than papered over with an exactly-once claim the two-phase
shape cannot honor. Shadow mode is the third unsynchronized path by design: it writes
the authoritative legacy row and emits the parallel events without a shared transaction,
because the point of shadow mode is to run the new path beside the old and compare, and
a shared transaction would hide exactly the divergence the comparator exists to surface.
The comparator's mismatch is the product, not a failure to suppress.

**Demo events are real store events: they ride the versioning seam and cross identity
by a deterministic mapping.** The patterns append to the real `PostgresEventStore`, not
a demo double, so the events are stamped at their current schema version through the
ADR 0050 write-stamp seam and would upcast on read like any other, and the strangler's
placements load and save the `Order` aggregate through the ADR 0051 snapshotting
repository the same way a host does. Identity crosses the legacy boundary through a
deterministic mapping owned by the translator: the legacy bigint order id maps to the
`Order` stream's Guid, and the legacy text customer name maps to the customer Guid the
events carry, both stable and injective. Because the mapping is deterministic and one
place owns it, an order has one event stream whatever pattern or side produced it: a
CDC-translated order, an outbox-drained order, and a strangler-routed order at the same
legacy id all land on the same stream. The translator also owns one vocabulary
decision, that a legacy status of `paid` is what the domain records as `OrderPlaced`;
it is a mapping the translator holds, not a fact the legacy schema names, and it is the
one place the legacy-to-domain vocabulary is decided. Rejected: a per-pattern identity
scheme, which would scatter one order across several streams and defeat the point of
migrating it; a migration needs a single durable identity map, and the demo carries one.

## Consequences

- CDC and outbox-on-legacy append below the pipeline, so a translated historical event
  is never vetoed by an aggregate invariant or deduped by an idempotency behavior; the
  strangler dispatches through `ICommandBus`, so a routed new placement runs the same
  guards the production hosts run.
- The demo drains its legacy outbox with its own emitter, so the shipped
  `OutboxProcessor` implementations stay untouched and no cross-adapter drain
  abstraction is introduced ahead of the engine that would force it.
- Both delivery paths are at-least-once and say so; a reader adapting them to a real
  system knows to make the consuming side idempotent rather than trusting an
  exactly-once guarantee the shape does not provide.
- Every pattern writes to the real event store on one deterministic identity map, so an
  order migrated by any pattern rehydrates as one aggregate, and the demo exercises the
  ADR 0050 versioning seam and the ADR 0051 snapshotting repository rather than a
  parallel test-only store.
- Shadow mode's comparator reports agreement or a named diverged field, so the demo
  shows both verdicts on one order and divergence reads as evidence rather than as an
  error the run swallows.

## Trigger for revisiting

A second relational engine growing its own outbox drain reopens the shared-drain
question ADR 0004 defers, and at that point the demo's emitter and the two shipped
processors are three instances of one shape, the count at which the abstraction earns
its keep. Until then re-implementing the pattern is the honesty a teaching artifact
owes its reader.

A migration that had to preserve the legacy id as the aggregate id, rather than map it,
reopens the deterministic-mapping decision: the demo maps because its legacy ids are
bigints and its aggregates are Guid-keyed, and a system whose legacy keys are already
Guids would carry them across unchanged and retire the mapping.

A demo pattern that had to enforce an invariant on a translated event, rather than
record it, reopens the below-the-pipeline entry: translation stays below the pipeline
while history is trusted, and a migration that had to reject malformed legacy history at
the boundary would need a validation seam the append path does not have.
