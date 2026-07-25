# Migration Patterns Demo

This is the Chapter 18 teaching artifact: a small CRUD-shaped legacy order system and
four patterns that carry it toward event sourcing. It is standalone from the reference
system's own hosts, and it exists to be read and run, not to migrate a production
database. Each pattern turns legacy order writes into domain events on the same event
store the rest of the repository uses, and the demo narrates each one on the console.

## Running it

You need Docker. From the repository root:

```
cd src/Migration
docker compose up
```

The compose file stands up its own PostgreSQL on host port `5433` (clear of the main
compose's `5432`, so both can run at once) and one `demo` container. The demo waits for
the database, creates and migrates its event store, applies the legacy schema, then runs
all four patterns in sequence and prints what it did. The run is bounded: it exits when
the last pattern finishes.

What you will see, per pattern: the legacy writes it made, what the pattern did with
them, and the events the store now holds on each order's stream. Shadow mode prints two
comparator verdicts, a match and then a deliberate mismatch, so the divergence check is
visible in both states.

To run one pattern instead of all four, pass its name:

```
docker compose run --rm demo cdc
```

The scenario names are `cdc`, `outbox`, `strangler`, `shadow`, and `all`. When you are
done, tear the demo down and remove its data volume:

```
docker compose down -v
```

## The simulated legacy system

The legacy side is a plain relational order system with no events of its own:
`legacy.orders` holds one row per order (a bigint id, a customer name, a status, a
total), and `legacy.order_lines` is its child table. It is a teaching artifact, a
stand-in for the CRUD-shaped system a real migration starts from, not a schema to copy.

Two of the four patterns read the legacy side through a change-tracking table,
`legacy.legacy_changes`, that a trigger on `legacy.orders` fills: every insert, update,
and delete appends one row carrying the operation and the row image as JSON, in commit
order. The schema lives in `src/Migration/Migration.Demo/Legacy/legacy_schema.sql`, and
`LegacySchemaApplier` applies it at startup.

## The patterns

Every pattern maps a legacy order to a domain event stream through one deterministic
identity map, so an order has a single stream whatever pattern produced it. The
`LegacyChangeTranslator` owns that map: the bigint order id becomes the `Order` stream's
Guid, and the customer name becomes the customer Guid the events carry. It also owns one
vocabulary decision, that a legacy status of `paid` is what the domain records as
`OrderPlaced`; that is a mapping the translator holds, not a fact the legacy schema
states. A real migration needs exactly this: a single durable identity map, so the order
you moved is one aggregate on the other side and not several.

### Change Data Capture (CDC)

**What it is.** A process reads a change-tracking log the database keeps and turns each
change into a domain event. The application code is untouched: the trigger records the
changes, and the reader consumes them.

**When to use it.** When you cannot change the legacy write path, or will not, and the
database can be made to record its changes. CDC is the least invasive of the four: the
cost it carries is a change-tracking mechanism on the database, here a trigger, and a
reader that understands the legacy row shape.

**How the demo shows it.** `CdcScenario` writes plainly to `legacy.orders` (insert an
order, update it to paid, insert another), which fires the trigger into
`legacy.legacy_changes`. `CdcReader` reads the change rows past its checkpoint,
translates each through `LegacyChangeTranslator`, and appends the events, then advances
the checkpoint. The console shows the change count consumed and the events on each
stream.

**Trade-offs.** CDC reads the row image after the fact, so it infers the business event
from a state change rather than being told it. It is at-least-once: the reader advances
its checkpoint only after the appends commit, and the checkpoint and the event store are
separate databases, so a crash between them replays the batch and the append side does
not dedupe. Make the consuming side idempotent.

### Outbox on legacy

**What it is.** The legacy code writes its state change and a serialized domain event to
an outbox table in the same transaction, and an emitter drains the outbox into the event
store. The event cannot be lost relative to the state change, because the two are one
transaction.

**When to use it.** When you can change the legacy write path. The application knows the
business event it is performing, so it stores the event itself rather than leaving a
reader to infer it from a row image. The cost it carries is the opposite of CDC's: no
database change-tracking, but the legacy code has to be edited to write the outbox row.

**How the demo shows it.** `LegacyOrderService` writes the CRUD row and the serialized
domain event to `legacy.legacy_outbox` in one transaction, serializing through the same
event-type registry and JSON options the store uses, so the rows deserialize identically
on the drain side. `LegacyOutboxEmitter` reads the unemitted rows, deserializes each,
appends them, and stamps `emitted_utc`. `OutboxScenario` shows the unemitted count, the
drained count, and the resulting stream.

**Trade-offs.** It touches the legacy code, which CDC does not. It is at-least-once for
the same reason CDC is: the emitter stamps `emitted_utc` only after the appends commit,
across two databases, so a crash between them redrains. The demo runs its own emitter
rather than the shipped `OutboxProcessor`, which drains the event store's own outbox and
was never written for a foreign table.

### Strangler

**What it is.** Two implementations of the same feature run side by side, and a router
sends each request to one or the other, so traffic shifts from the legacy system to the
new one by changing the router rather than by a cutover.

**When to use it.** When you want to migrate incrementally and reversibly, moving a
slice of traffic at a time and keeping the option to route back. It is the pattern for a
migration that has to stay live.

**How the demo shows it.** `StranglerRouter` routes by a predicate on the order id, even
ids to the event-sourced application and odd ids to the legacy service. The event-sourced
side dispatches through `ICommandBus` and the full command pipeline, the sequence a real
placement needs (draft, add a line, set a shipping address, place), because a routed
order is a new decision and a new decision runs the same guards the production hosts run.
The legacy side calls `LegacyOrderService`. `StranglerScenario` routes one order each
way and shows the event-sourced order's stream and the legacy order's row.

**Trade-offs.** The route must be a pure function of durable identity, evaluated once per
order, so an order's whole history lands on one side; a route that varied across an
order's calls, a coin flip or a moving percentage keyed on the clock, would split one
order across two systems, the one thing a strangler must never do. The demo dispatches
without an idempotency key, on the bus's null-key path; a production strangler would
thread a durable key derived from the legacy id through both sides, so a retried route
does not apply twice. The null-key call is where that key belongs.

### Shadow mode

**What it is.** The new implementation runs in parallel with the authoritative old one,
emitting its events beside the legacy writes without being authoritative, and a
comparator checks the two agree. It is how a team earns confidence in the new path before
cutting over to it.

**When to use it.** Before a cutover, to prove the new implementation matches the old on
real traffic while the old one is still the source of truth.

**How the demo shows it.** `ShadowOrderService` performs the authoritative legacy write,
then emits the parallel events into the store. `ShadowComparator` is a pure function that
folds the events into the legacy row shape and reports a match or the diverged field; it
compares identity, customer, status, and total, and ignores timestamps, which the two
sides record independently. `ShadowScenario` shows the comparator agreeing on a faithful
order, then makes a legacy-only status change with no matching event and shows the
comparator naming the divergence.

**Trade-offs.** The legacy write stays authoritative and the two writes are not
synchronized, on purpose: a shared transaction would hide the divergence the comparator
exists to find. The divergence is the product of shadow mode, not a failure to suppress.
The comparator here compares the demo's small order shape; a real one grows to cover the
fields that matter to the migration.

## Where to read more

The reasoning behind these decisions, where each pattern's events enter the
event-sourced side, why the legacy drain is a demo-local emitter, and how identity and
versioning cross the boundary, is recorded in
[ADR 0052](../../docs/adr/0052-migration-demo-entry-surfaces-and-delivery-postures.md).
The executable contract for each pattern is its tests in `tests/Migration.Tests` and the
shadow comparator's properties in `tests/PropertyTests`.
