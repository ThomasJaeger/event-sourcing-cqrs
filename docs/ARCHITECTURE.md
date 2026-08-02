# Architecture

The cross-cutting decisions: what they are, how they compose, and which ADR owns each one.

**This document routes. It does not restate.** Fifty-two ADRs in `docs/adr/` carry the
decisions and the reasoning behind them. A summary that repeated them would become a second
copy that drifts, and a drifted second copy is worse than no copy, because a reader cannot tell
which one is current. So nothing here can go stale on its own: every sentence is either a
routing pointer or a statement about how two decisions compose, and either way it changes only
when an ADR does.

Thirty of the fifty-two are cross-cutting, meaning honoring them requires agreement between
components that could otherwise change independently. The other twenty-two settle one
component and are reachable from the corpus directly. This document covers the thirty.

## Layering

Domain sits at the center with no I/O. Application depends on Domain and Domain.Abstractions
and nothing else. Infrastructure implements what Domain.Abstractions declares. The four hosts
depend on Application. `CLAUDE.md` states the rule; the compiler enforces it through project
references.

Two decisions place types that could plausibly have gone elsewhere. Read-side store interfaces
live with their bounded context rather than in Domain.Abstractions, so `IOrderListStore` sits
under `src/Domain/Sales/ReadModels/` and only context-agnostic ports are central (ADR 0008,
Read-Side Store Interfaces Live With Their Bounded Context). `CommandTypeRegistry` lives in
`src/Domain.Abstractions/` rather than in an adapter, because every host registers into it and
an adapter-resident registry would have pulled hosts toward a storage project (ADR 0026,
CommandTypeRegistry lives in Domain.Abstractions).

Where any of this competes with teaching clarity, production quality wins, and that priority
overrides framing anywhere else in the repository (ADR 0025, Production quality over teaching
clarity).

## Identity and streams

Aggregate and cross-context identifiers are raw `Guid` (ADR 0005, Raw Guid for Aggregate and
Cross-Context Identifiers). Stream identifiers are not: `StreamId` is a typed wrapper carrying
a short prefix that names the stream's role, so an aggregate stream and a process-manager
stream sharing a `Guid` do not collide, and prefix-family routing has something to route on
(ADR 0011, Typed StreamId With Type-Prefix Convention).

`TenantId` is the second typed exception to the raw-`Guid` convention, taken on security
grounds rather than style (ADR 0029, Typed TenantId as a Security Exception to the Raw-Guid
Convention).

## Context boundaries

Money is jointly owned by Sales and Billing as a shared kernel (ADR 0006, Money is a Shared
Kernel Between Sales and Billing). Line types are deliberately not shared: each context defines
its own, and the apparent duplication is the boundary doing its job (ADR 0007, Line Types Stay
Per Context).

`docs/architecture/cross-context-vocabulary.md` is the worked example of what crosses each
boundary and what does not.

## The event store port and its four peers

`IEventStore` in `src/Domain.Abstractions/IEventStore.cs` is the port. Four adapters implement
it as shipped peers, and a fifth, `InMemoryEventStore`, serves tests and the migration demo
without being a configurable provider.

**What the port guarantees.** Append is atomic per stream with optimistic concurrency on the
expected version. Global position is monotonic in commit order, and gaps come only from
rolled-back appends and are permanent rather than transient (ADR 0044, Commit-Ordered Global
Position). Every adapter must produce that ordering, whatever its engine offers natively.

**What enforces it.** `tests/EventStore.ContractTests` is one suite that all four adapters
pass. It is the definition of done for an adapter, not a per-adapter convention.

**Where adapters may differ.** Everywhere below the port. Adapters are self-contained: each
owns its row construction, its concurrency translation, and its dispatch mechanics, and no
shared relational layer exists (ADR 0004, Self-Contained Event Store Adapters). The
engine-specific mappings that follow from that are per-adapter records: ADR 0045 (SQL Server
Adapter Engine Mappings), ADR 0047 (KurrentDB Adapter Engine Mappings), ADR 0049 (DynamoDB
Adapter Engine Mappings).

Companion ports beside the event store, the idempotency store and the delay queue among them,
take a posture of their own on the non-relational engines (ADR 0046, Non-Relational Companion
Port Posture).

## The write path

A command enters through `ICommandBus`, which opens a scope per dispatch so scoped services
resolve per command rather than per host lifetime (ADR 0024, Scope-per-dispatch on command and
query buses).

Four pipeline behaviors run, in the order `AddApplication` registers them, all under
`src/Application/Pipelines/`: logging, authorization, idempotency, validation. Authorization
sits inside logging and before idempotency, so an unauthorized attempt is logged and consumes
no idempotency storage (ADR 0028, Permission-Based Authorization Model). Idempotency keys are
opt-in per command and dedupe a retry (ADR 0016, Idempotency-Key Mechanism), threaded from the
user surface through both hosts (ADR 0021, Idempotency-Key Threading on the User-Dispatch
Path).

The handler loads an aggregate through `IEventStoreRepository<TAggregate>`, calls a command
method, and saves. The repository builds envelopes and appends. Events and their outbox rows
land in one transaction on the relational adapters. Process-manager events take the same table
and skip the outbox, because they are internal coordination state rather than an integration
contract (ADR 0013, PM Events Same Table, No Outbox).

## The read path

The outbox drains to in-process handlers. Each handler receives `EventContext<TEvent>` rather
than a bare payload, so metadata travels with the event (ADR 0010, Consumer-Side Event Handler
Dispatch Shape), and the dispatch path carries per-handler caused-event context so a projection
that raises further work keeps the causation chain intact (ADR 0042, Per-Handler Caused-Event
Context on the Outbox Dispatch Path).

Only the relational adapters have an outbox. KurrentDB uses native catch-up subscriptions
through `KurrentSubscriptionService`, and DynamoDB uses Streams through
`DynamoDbStreamDispatchService`. The projection contract does not change with the trigger.

Projections are pull-based with their own checkpoints, idempotent on re-read, and never call
back into the write side. A projection needing an identifier another aggregate owns resolves it
through a projection-private lookup rather than by widening an event's payload or by querying
across projections (ADR 0020, Projections Resolve Cross-Aggregate Identity Through
Projection-Private Lookups).

Read models live in PostgreSQL whichever engine holds the events. Queries reach them through
context-resident store ports (ADR 0008).

## Process managers

Process managers are event-sourced with their own streams, and their type hierarchy is separate
from the aggregate one: `ProcessManager` rather than `AggregateRoot`, `IProcessManagerEvent`
rather than `IDomainEvent`, `IProcessManagerRepository<TPm>` rather than
`IEventStoreRepository<TAggregate>` (ADR 0012, Process Manager Type Hierarchy).

The separation is a compile-time boundary, not a convention. `IProcessManagerEvent` derives
from nothing, so no conversion to `IDomainEvent` exists, and `IEventStore` takes the
distinction into its own signatures. That is what keeps a process-manager event off the outbox
by construction rather than by review, which is what ADR 0013 requires.

A process manager dispatches commands through `CausedCommandBus`, which shares the user path's
pipeline and accessor machinery while supplying context rather than minting it (ADR 0014,
CausedCommandBus for Process Managers). Its handler contract and the way a failed command
signals back are ADR 0015 (Process-Manager Handler Contract and Command-Failure Signaling).
Timeouts come from a delay queue holding scheduled commands (ADR 0017, Timeouts via a
PostgreSQL Delay Queue).

## Multi-tenancy

Tenant isolation is a shared-schema discriminator, enforced by infrastructure rather than by
per-query discipline (ADR 0031, Tenant Isolation by a Shared-Schema Discriminator). The tenant
is a typed `TenantId` (ADR 0029), it rides in `EventMetadata` on every event, and the existing
corpus was migrated to a default tenant by an additive backfill that left historical stream
identifiers intact (ADR 0030, EventEnvelope Tenant and the Corpus Migration).

It reaches each path differently, and this is the composition worth knowing. On the write path
the tenant is read from the principal at the HTTP edge and stamped into metadata. On the read
path a per-store predicate reads the current tenant through `ICurrentTenantAccessor`, so a
query that forgot to filter still cannot see another tenant's rows. In stream identifiers the
tenant sits after the prefix, so ADR 0011's prefix-family routing survives. At the worker edge
the tenant comes from metadata rather than from a principal, because no principal exists there.
Rebuilds are per-tenant through `PerTenantProjectionRebuilder`.

## Authorization

Permissions, not role names, are what code checks. Role-to-permission mapping is
startup-validated config; user-to-role assignment is an event-sourced Access context. Command
authorization is a pipeline behavior, query and read-model authorization is row filtering, and
subscription authorization is a resource-ownership check (ADR 0028). Subscription resources are
owner-scoped (ADR 0037, Owner-Scoped Subscription Resources).

The two hosts take different postures, and both are recorded: the Web host gates admin pages at
the Api trust boundary rather than at the route (ADR 0038, Web-Host Admin-Page Authorization
Posture), and the AdminConsole host is deny-by-default behind a declarative fallback policy
(ADR 0040, AdminConsole Host Authorization Posture). ADR 0040 supersedes ADR 0038 for pages
behind that gate.

## Notification dispatch for live UI

Dashboards update through in-process notification dispatch. The pages render server-side on the
host that already receives every projection-commit notification, so a notification reaches them
without a network round trip (ADR 0032, In-Process Notification Dispatch for Live Dashboard
Updates). List dashboards subscribe by collection (ADR 0033, Collection-Scoped Notification
Subscriptions for List Dashboards), a page owns its subscription's liveness (ADR 0034,
Page-Owned Subscription Liveness and the Shared LiveBadge), and ownership is checked before a
subscription is granted (ADR 0037).

**The closed no-go.** A SignalR hub with a PostgreSQL LISTEN/NOTIFY carrier was the prior
design and is superseded (ADR 0027, SignalR Hub Topology with PostgreSQL LISTEN/NOTIFY
Carrier). ADR 0032 records why the three transports it considered were rejected: a server-side
loopback connection from the host back to its own hub is circular, a hub retained as test-only
code is runtime-dead, and a browser client is the path an out-of-process consumer would take
and no such consumer exists. The backplane surface that survived is `IHubBackplaneConnection`
in `src/Application/SignalR/`, named in ADR 0032's amendment.

## Versioning, upcasting, and snapshots

An event's version derives from chain topology rather than from a hand-maintained number, and
the upcaster pipeline lifts a stored shape to the current one at read time without ever
mutating what is stored (ADR 0050, Event Versioning and the Upcaster Seam). The seam that
serves it is consolidated in `src/Infrastructure/Versioning/` rather than duplicated per
adapter, which is the one place ADR 0004's revisit trigger fired and factoring won (ADR 0048,
Versioning Seam Consolidation).

Snapshots take the opposite strategy for their own schema changes: an old snapshot is discarded
and rebuilt rather than upcast, because a snapshot is derived state and replaying is cheaper
than maintaining a second upcaster chain (ADR 0051, Order Aggregate Snapshotting and the
Snapshotting Repository). The snapshot leg's timeout is discriminated from the replay leg's so
a slow snapshot read is not reported as a slow replay (ADR 0036, Snapshot-Leg Timeout
Discrimination).

## Migration runners

Each relational adapter carries its own migration runner, self-contained per ADR 0004:
`MigrationRunner` for PostgreSQL and `SqlServerMigrationRunner` for SQL Server. They are
structurally twins and share no code, because the lock primitive, the existence probe, and the
batch handling are all engine-specific.

Both refuse rather than proceed on three conditions: a file edited after being applied, two
files claiming one version, and a pending migration numbered below the highest applied version
(ADR 0053, Migration Runner Refusal Guarantees). That ADR also records which of the three each
runner has a fact for, which is not all of them, and why contiguity and hole detection were
declined.

## Where decisions interact

The highest-value content here, because in each case both ADRs are correct alone and neither
names the other.

**Commit-ordered position and the upcaster seam.** ADR 0044 fixes the order a reader sees
events in; ADR 0050 fixes the shape it sees them in. Every projection read is governed by both
at once: it walks positions in commit order and passes each event through the upcaster on the
way out. Neither ADR cites the other. A change to either that assumed sole ownership of the
read would be wrong.

**Commit-ordered position and tenant isolation.** ADR 0044 gives a global ordering; ADR 0031
gives a per-tenant view. A per-tenant rebuild needs both to hold simultaneously: it reads a
position range in commit order while a tenant predicate filters it. Neither ADR cites the
other, and the code that has to satisfy both is `PerTenantProjectionRebuilder`.

**Projection-private lookups and commit ordering.** ADR 0020 lets a projection resolve a
foreign identifier through a private lookup. That lookup is itself a projection with its own
checkpoint, so whether it has seen the event the consuming projection needs is a question about
relative positions, which is ADR 0044's subject. Neither cites the other.

**Scope-per-dispatch and the caused command bus.** ADR 0024 requires a scope per dispatch and
names the caused-command-bus adapter as a future dispatcher that must follow the same shape.
ADR 0014 then forbade a parallel dispatcher outright, so the caused path opens no scope of its
own and instead runs the one dispatch loop the user path runs. Scope-per-dispatch therefore
holds for process-manager dispatch structurally rather than by conformance, and dropping the
sharing would produce captive dependencies on the process-manager path alone. ADR 0024's
amendment records it.

## Reading the corpus

ADRs are numbered and immutable. A decision that changes gets a new ADR that supersedes the old
one, and the old one's status says so. A decision that gains detail without changing gets an
appended `## Amendment` section on the original, and its status is left alone. Both forms are
in use; ADR 0027 is the first shape and ADR 0025 the second.

`CLAUDE.md` carries the repo-wide rules and the folder layout. `docs/PLAN.md` carries the build
sequence and what ships in v1. `docs/architecture/cross-context-vocabulary.md` is live
documentation of the context boundaries; the four `phase-N-readiness.md` files beside it are
historical records of phase-entry verifications, not current guidance.
