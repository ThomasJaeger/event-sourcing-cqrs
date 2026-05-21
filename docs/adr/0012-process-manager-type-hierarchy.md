# 0012. Process Manager Type Hierarchy

## Status

Accepted (May 2026)

## Context

`src/Domain.Abstractions/AggregateRoot.cs` is the base class for aggregates: `Guid Id`, `int Version`, a private `List<IDomainEvent>` uncommitted-events buffer, a `Raise(IDomainEvent)` method that applies the event to internal state, enqueues it, and increments `Version`, an `ApplyHistoric(IDomainEvent)` method that applies and increments without enqueuing (for rehydration), an abstract `Apply(IDomainEvent)` method that derived aggregates override, and a `DequeueUncommittedEvents()` method that returns the buffered events and clears the buffer in one call. `IDomainEvent` is a bare marker interface; metadata lives on `EventEnvelope`. Aggregate persistence routes through `IEventStoreRepository<TAggregate>` in `src/Domain.Abstractions/`, with the concrete repository in Infrastructure. Event-type registration uses `IEventTypeProvider` in `src/Domain.Abstractions/` with the concrete `EventTypeRegistry` in `src/Infrastructure/EventStore.Postgres/`.

Process managers need behavior that aggregates don't share. PMs consume events from other aggregates, dispatch commands in response, persist their own state as events on a dedicated stream, and live a different lifecycle: a PM handler is a short-lived operation that loads or creates a PM, applies a transition, persists, and dispatches downstream commands. Reusing `AggregateRoot` would conflate two type families that have meaningful behavioral differences. The conflation cost surfaces in three places. First, `IDomainEvent` would carry both aggregate events and PM-internal events with no type-system distinction, which weakens the read-side prefix-routing introduced by ADR 0011: the registry can route by stream-ID prefix, but the in-memory type model would carry no signal. Second, the aggregate's `DequeueUncommittedEvents` (returns-and-clears in one call) suits the aggregate lifecycle's discard-on-failure pattern but is less conservative than the PM lifecycle wants: a PM whose persist fails after the dequeue has cleared its buffer has lost track of what wasn't saved. Third, the aggregate's `Id` is `Guid`, but PMs are introduced alongside the typed `StreamId` from ADR 0011 and adopt the typed shape from the start, which doesn't fit `AggregateRoot.Id`'s signature.

A single-hierarchy alternative was considered: PMs extend `AggregateRoot` and use `IDomainEvent` for their internal events, distinguishing PMs from aggregates only by convention (e.g., a marker interface on top of `AggregateRoot`). This was rejected. The behavioral differences above are real, and surfacing them through the type system at the cost of some parallel machinery is preferable to relying on convention to enforce them. The reference implementation's pedagogical value depends on showing the distinction cleanly rather than papering over it.

## Decision

Process managers extend a new `ProcessManager` base class in `src/Domain.Abstractions/`, distinct from `AggregateRoot`. PM-internal events implement a new `IProcessManagerEvent` marker interface, distinct from `IDomainEvent`. `IProcessManagerEvent` is bare, mirroring `IDomainEvent`; metadata stays on the envelope. The `ProcessManager` base carries: a `StreamId StreamId` property (typed per ADR 0011, in contrast to `AggregateRoot.Id`'s raw `Guid` per ADR 0005), an `int Version` property (matching the aggregate's `int` typing and the `IEventStore` contract's `int expectedVersion`), and a private `List<IProcessManagerEvent>` uncommitted-events buffer. The base method surface diverges deliberately from the aggregate's:

```csharp
protected void RecordTransition(IProcessManagerEvent evt);
public void LoadFromHistory(IEnumerable<IProcessManagerEvent> history);
public IReadOnlyList<IProcessManagerEvent> GetUncommittedEvents();
public void MarkCommitted();
protected abstract void Apply(IProcessManagerEvent evt);
```

The `GetUncommittedEvents` and `MarkCommitted` split, rather than the aggregate's single `DequeueUncommittedEvents`, lets the repository clear the buffer only after a successful append. A persist failure leaves the in-memory PM with its events intact, and a retry uses the same buffer. This is more conservative than the aggregate's dequeue-on-read pattern; the PM lifecycle benefits more from that conservatism than the aggregate lifecycle does.

PM persistence routes through a new `IProcessManagerRepository<TPm>` interface in `src/Domain.Abstractions/`, parallel to `IEventStoreRepository<TAggregate>`:

```csharp
public interface IProcessManagerRepository<TPm> where TPm : ProcessManager
{
    Task<TPm?> LoadAsync(StreamId streamId, CancellationToken ct);
    Task<TPm> LoadOrNewAsync(StreamId streamId, Func<StreamId, TPm> factory, CancellationToken ct);
    Task SaveAsync(TPm pm, CancellationToken ct);
}
```

`LoadAsync` returns nullable for callers that want explicit existence-check semantics (timeout-command handlers and compensation paths load a PM that must already exist). `LoadOrNewAsync` centralizes the load-or-create pattern PM handlers hit on workflow-initiating events (`OrderPlaced` for `OrderFulfillmentProcessManager`, `ShipmentReturned` for `ReturnProcessManager`). The `Func<StreamId, TPm>` factory keeps PM construction in the caller's hands without requiring a public parameterless constructor.

PM event-type registration parallels the aggregate-side split: `IProcessManagerEventTypeProvider` lives in `src/Domain.Abstractions/`, and the concrete `ProcessManagerEventTypeRegistry` lives in `src/Infrastructure/EventStore.Postgres/`. The placement matches the existing `IEventTypeProvider` and `EventTypeRegistry`.

## Consequences

- Four new files in `src/Domain.Abstractions/` (flat): `ProcessManager.cs`, `IProcessManagerEvent.cs`, `IProcessManagerRepository.cs`, and (registered alongside the registry in the next foundational commit) `IProcessManagerEventTypeProvider.cs`. The concrete `ProcessManagerRepository<TPm>` lives in `src/Infrastructure/EventStore.Postgres/` alongside `EventStoreRepository`. The concrete `ProcessManagerEventTypeRegistry` lives in the same Infrastructure project alongside `EventTypeRegistry`.
- ADR 0013 inherits the `int expectedVersion` typing on `AppendProcessManagerEventsAsync(StreamId, int expectedVersion, IEnumerable<IProcessManagerEvent>, CancellationToken)` to match `AppendAsync` and the PM base's `int Version`.
- PM event naming follows the `{Action}{State}` convention: `OrderFulfillmentStarted`, `PaymentAuthorizationRecorded`, `OrderFulfillmentCompleted`. Event discovery extends the existing per-bounded-context pattern; PM event types are discovered through `IProcessManagerEventTypeProvider` implementations registered per PM type rather than per bounded context.
- A `ProcessManagerTestHarness<TPm>` test helper lands alongside the first PM implementation (not in this ADR's commits). The harness mirrors the construct-context-and-invoke shape established by the projection tests; PM tests use it for the multi-event flows projection tests don't exercise.
- Track A flag against Chapter 10's `ch10_processManager_code`. Chapter 10 describes process managers as a concept distinct from aggregates but does not depict that separation at the type-system level, and the persistence it depicts is a state-store shape (`IProcessManagerStore`) rather than the event-sourced repository this ADR commits to. Phase 14 reconciliation updates the depicted shape to the `ProcessManager` base with `IProcessManagerEvent` and `IProcessManagerRepository<TPm>`. Chapter 10's pedagogy, the state-guard discipline, idempotency, timeouts, and compensation paths, survives the reconciliation unchanged.

## Trigger for revisiting

The type-hierarchy separation is reversible. Conditions that would justify reopening it:

- The behavioral differences between aggregates and process managers narrow to the point where a single hierarchy with convention-based distinction (a marker interface on top of `AggregateRoot`) carries enough information for production tooling, snapshot infrastructure, and operational observability. The current roadmap through Phase 12 doesn't approach this threshold; a hypothetical future shape change might.
- The parallel-machinery cost (separate repository, separate registry, separate test harness) becomes a maintenance burden that the type-system signal doesn't justify. The cost is bounded by the count of consumer-side abstractions in the codebase, which is small and stable; this trigger is unlikely to fire absent a substantial codebase expansion.
