# 0026. CommandTypeRegistry lives in Domain.Abstractions

## Status

Accepted (May 2026)

## Context

ADR 0023 placed `CommandTypeRegistry` and `UnknownCommandTypeException` in
the `EventSourcingCqrs.Infrastructure.EventStore.Postgres` assembly,
co-located with `EventTypeRegistry` and `ProcessManagerEventTypeRegistry`.
At the time, the registry's only consumers lived inside hosts that
composed the Postgres event store: the Api host (which calls
`AddPostgresEventStore` and exposes `POST /commands`) and the Workers
host (which calls `AddPostgresEventStore` and runs the delay-queue
processor that resolves scheduled command types). The placement was
defensible: every consumer reached the type through an existing
`EventStore.Postgres` reference.

ADR 0023 also noted that all three registries were slated to move to a
shared `Infrastructure/Versioning` assembly in Phase 15, alongside the
upcasting pipeline and schema-registry stub. That migration was framed
as a collective move covering all three registries together.

Phase 7's Web host changes the consumer set. The Web host is a Blazor
Server project that dispatches commands over HTTP to the Api host's
`POST /commands` endpoint via an `IApiClient` typed HTTP client. The
Web host needs `CommandTypeRegistry` to build the type-discriminator
envelope at dispatch time. Under the current placement, the Web host
must take a project reference on `EventStore.Postgres` to see the type,
which pulls in Npgsql, the event-store implementation, the outbox
machinery, the delay queue, and every other Postgres-specific concern
the Web host neither uses nor configures. The Web host has no
event-store or read-model connection string; it dispatches over HTTP
and resolves no bus, no handler, no event store. Pulling the full
event-store stack for one type is a production-quality violation per
ADR 0025: the Web host would compose infrastructure it cannot
configure, and a deployment defect in the Postgres connection string
would surface as a Web-host startup failure for code the Web host does
not execute.

Three placement options were considered:

- **Stay in `EventStore.Postgres`.** Forces the Web host's project
  reference on Postgres infrastructure. Rejected on production-quality
  grounds per ADR 0025.

- **Move to `Application`** alongside `QueryTypeRegistry` (ADR 0022).
  Symmetric in shape with `QueryTypeRegistry`'s placement and
  rationale ("the concern is transport, not persistence"). Rejected
  because `CommandTypeRegistry` has a persistence consumer that
  `QueryTypeRegistry` does not: `PostgresDelayQueue` and
  `DelayQueueProcessor` both take the registry as a constructor
  dependency to resolve scheduled command types. Moving the registry
  to `Application` forces `EventStore.Postgres` to reference
  `Application`, inverting the hexagonal layering rule (Infrastructure
  must not depend on Application, the layering `docs/ARCHITECTURE.md`
  records and the compiler enforces). The alternative of moving the registration to
  `AddApplication` while keeping the type in `Application` leaves
  `AddPostgresEventStore` unable to resolve `IDelayQueue` without
  `AddApplication` also being called, which couples two extension
  methods that were deliberately separable per ADR 0008 and the
  Cluster 2 Commit 8 outbox split.

- **Move to `Domain.Abstractions`** alongside `ICommandTypeProvider`,
  `IQueryTypeProvider`, and `IEventTypeProvider`. Every consumer
  reaches the type through an existing `Domain.Abstractions`
  reference: the Api host transitively via `Application`, the Web host
  transitively via `Application`, `EventStore.Postgres` directly. No
  new dependency edges. No registration relocation. No layering
  inversion.

## Decision

`CommandTypeRegistry` and `UnknownCommandTypeException` live in
`src/Domain.Abstractions/`, in the namespace
`EventSourcingCqrs.Domain.Abstractions`. Co-located with
`ICommandTypeProvider`.

`AddPostgresEventStore` continues to register the registry as a
singleton, walking the registered `ICommandTypeProvider` instances at
first resolution. The registration code does not move; only the type's
namespace changes. The Web host composes its own registry instance
inline in `Program.cs`, walking the providers it registers
independently, without calling `AddPostgresEventStore` or
`AddApplication`.

`QueryTypeRegistry` stays in `Application` per ADR 0022. The
asymmetry between the two registries' placements is justified by
their asymmetric consumer sets:

- `QueryTypeRegistry` has only transport consumers (the Api host's
  `POST /queries` endpoint and the Web host's `IApiClient`). No
  infrastructure consumer means no inversion when the type lives in
  `Application`.

- `CommandTypeRegistry` has both a transport consumer (the Api host's
  `POST /commands` endpoint, the Web host's `IApiClient`) and a
  persistence consumer (`PostgresDelayQueue`, `DelayQueueProcessor`).
  The persistence consumer requires the type to live below
  `Application` in the dependency graph; `Domain.Abstractions` is the
  layer that satisfies both consumer sets without inversion.

The Phase-15 collective-move expectation in ADR 0023 is amended. Only
`CommandTypeRegistry` moves now, and to `Domain.Abstractions` rather
than `Infrastructure/Versioning`. `EventTypeRegistry` and
`ProcessManagerEventTypeRegistry` stay in `EventStore.Postgres` and
their Phase-15 disposition stays open; each is consumed only by hosts
that already compose the Postgres event store, so neither has the
Defect-1 reach problem `CommandTypeRegistry` had. The collective-move
framing in ADR 0023 was defensible at the time of writing and is
superseded for `CommandTypeRegistry` only by this ADR.

## Consequences

- The Web host references `CommandTypeRegistry` through its existing
  `Application` project reference, with no Postgres infrastructure
  pulled into the Web host's composition graph. The
  production-quality violation the prior placement would have forced
  on the Web host is resolved.

- `EventStore.Postgres`'s registration of `CommandTypeRegistry` in
  `AddPostgresEventStore` works unchanged: the registration code
  references the type by simple name, the file already imports
  `EventSourcingCqrs.Domain.Abstractions`, and the registration
  semantics (`TryAddSingleton`, lazy walk of registered providers) are
  preserved. `PostgresDelayQueue` and `DelayQueueProcessor` resolve
  the registry unchanged.

- The `tests/Infrastructure.Tests/Postgres/CommandTypeRegistryTests.cs`
  file relocates to `tests/Domain.Tests/CommandTypeRegistryTests.cs`
  alongside other `Domain.Abstractions` type-behavior tests. The test
  uses no infrastructure and runs in microseconds, matching the
  Domain.Tests project's stated contract.

- ADR 0023's Phase-15 framing is amended for `CommandTypeRegistry`
  only. A future Phase-15 ADR that addresses `EventTypeRegistry` and
  `ProcessManagerEventTypeRegistry` (whether through a shared
  `Infrastructure/Versioning` move or through their own
  `Domain.Abstractions` placement under the same reasoning as this
  ADR) builds on this one rather than overriding it.

- The asymmetry between `CommandTypeRegistry` (in
  `Domain.Abstractions`) and `QueryTypeRegistry` (in `Application`) is
  recorded here as the deliberate consequence of asymmetric consumer
  sets. A future change that gives `QueryTypeRegistry` a persistence
  consumer (no such consumer is anticipated) would trigger the same
  reasoning and the same relocation.

- The Phase 17 manuscript reconciliation absorbs any chapter-prose
  divergence created by the relocation. The chapter does not currently
  depict the registry placement at the assembly granularity this ADR
  governs.

## Trigger for revisiting

- A second concrete type currently in `EventStore.Postgres` proves to
  have a transport consumer that does not compose the Postgres event
  store, suggesting that the `Domain.Abstractions` placement
  reasoning generalizes. The trigger is the second concrete case;
  one-off placement choices stand alone.

- A future event-store adapter (SQL Server in Phase 2, KurrentDB in
  Phase 13, DynamoDB in Phase 14) needs a registry of its own with
  different semantics from `CommandTypeRegistry`. If the per-adapter
  registry must live with the adapter, the unified-registry framing
  this ADR preserves needs to be reopened.

- The Phase 15 versioning work reveals that the upcasting pipeline
  needs the command-type registry directly. If
  `Infrastructure/Versioning` becomes a consumer, the placement
  rationale stays correct (Versioning would reference
  `Domain.Abstractions` like other infrastructure does), but the ADR
  should be re-read against the new consumer's needs.
