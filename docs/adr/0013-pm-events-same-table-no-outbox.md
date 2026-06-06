# 0013. PM Events Same Table, No Outbox

## Status

Accepted (May 2026)

## Context

`IEventStore.AppendAsync(Guid, int, IReadOnlyList<EventEnvelope>, CancellationToken)` writes events and their outbox rows inside one `NpgsqlTransaction` in `PostgresEventStore`, with the outbox-insert site at line 94 of the adapter. `EventEnvelope.Payload` is strictly typed as `IDomainEvent`. Read-side payload-type resolution uses a single `EventTypeRegistry`, called from `ReadStreamAsync` and `ReadAllAsync` at two sites (`_registry.TypeFor(eventType)`). The outbox row is self-describing (carries `event_type`, payload, metadata, `global_position`), so the `OutboxProcessor` never joins back to the events table.

PM events are internal coordination state, not integration contract. They have no external consumers: no projection observes a PM stream per this ADR's downstream commitment to that rule, and no other bounded context subscribes. Routing them through the outbox would write rows the dispatch path has no handlers for, accumulating either dead rows (handler-not-found exceptions in `OutboxProcessor`) or noise (no-op dispatches that consume processor capacity). Either outcome is operational debt for no benefit.

A "separate `process_manager_events` table" alternative was considered and rejected: parallel adapter implementations, two-store replay tooling, two-table queries for the Phase 12 Correlation-ID Tracer. The chosen direction (same table, separate write path, separate type registry routed by stream-ID prefix) requires three additions the disk doesn't carry today. A PM-specific envelope type to carry `IProcessManagerEvent` payloads, since `EventEnvelope.Payload` is strictly `IDomainEvent` and ADR 0012's `IProcessManagerEvent` does not extend `IDomainEvent`. A PM-specific read path, since `ReadStreamAsync` returns aggregate-typed envelopes the PM repository cannot use. And a filter on `ReadAllAsync`, since the projection feed must not see PM events. The setup-doc framing of "one additive append method" was incomplete; the read-side surface widens equivalently.

## Decision

PM events persist as `ProcessManagerEventEnvelope` records, parallel to `EventEnvelope` but with `Payload` typed as `IProcessManagerEvent`. The metadata type is `EventMetadata`, reused unchanged from the aggregate side; `EventMetadata` carries no payload reference and is fully reusable for PM events. The append method `AppendProcessManagerEventsAsync(StreamId streamId, int expectedVersion, IReadOnlyList<ProcessManagerEventEnvelope> envelopes, CancellationToken ct)` writes to the same `events` table as `AppendAsync`, honors the same `uq_events_stream_version` constraint, but skips the outbox write. The per-envelope loop runs the events insert and nothing else. The `int expectedVersion` typing matches `AppendAsync` and ADR 0012's `int Version` on the PM base.

PM stream rehydration uses a new `ReadProcessManagerStreamAsync(StreamId streamId, int fromVersion, CancellationToken ct)` returning `IReadOnlyList<ProcessManagerEventEnvelope>`. It resolves payload types through `ProcessManagerEventTypeRegistry` and guards that the supplied `StreamId` carries a `pm-` prefix, failing loudly per ADR 0011's rule if it does not. The aggregate read path keeps resolving through `EventTypeRegistry`. Each read method is typed for its event family, so the registry is selected by which method runs, not by per-row prefix inspection. The case that needs per-row routing, a single query disambiguating PM and aggregate rows in one pass, is the Phase 12 Correlation-ID Tracer's.

`ReadAllAsync` filters PM-prefixed streams. The method signature is unchanged; the semantics narrow from "all events" to "all events on non-PM streams." Phase 6 projections that want workflow-level state derive it from aggregate events, not PM streams. The filter applies at the query level (a `WHERE NOT (stream_id LIKE 'pm-%')` clause or equivalent), not at the post-read level, so the projection feed never materializes PM rows.

## Consequences

- `IEventStore` contract widens at three points: `AppendProcessManagerEventsAsync` (new), `ReadProcessManagerStreamAsync` (new), and `ReadAllAsync` (semantics narrowed). All three build on the `StreamId` contract from ADR 0011.
- A new `ProcessManagerEventEnvelope` record in `src/Domain.Abstractions/` mirrors `EventEnvelope` with `Payload: IProcessManagerEvent`. `EventMetadata` is reused unchanged.
- The PostgreSQL adapter's outbox write site (line 94 of `PostgresEventStore`) is reachable only from `AppendAsync`. `AppendProcessManagerEventsAsync` runs a parallel write path that omits the outbox insert. The shared `NpgsqlTransaction` wrapping pattern is preserved on the PM path; only the outbox row is skipped.
- The PM path introduces a second registry rather than extending the single one the adapter resolves through today. The registry is selected by which typed read method runs, not by per-row inspection; the only Phase-5 place a stream-ID prefix is examined is `ReadAllAsync`'s filter and the `pm-` guard on the PM read path. Unknown prefixes fail loudly at `StreamId` construction (ADR 0011) and at that guard.
- Phase 6 projections cannot observe PM event streams. This is an architectural rule, not a temporary state: even when `OrderListProjection` extends to handle workflow-level state in Phase 6, it derives that state from aggregate events (`OrderShipped`, `OrderCompleted`), not from PM stream observation. The Phase 6 readiness doc names this constraint explicitly.
- Phase 12's Correlation-ID Tracer queries the events table once for cross-event-type traces. PM rows appear in the trace results alongside aggregate rows, joined by `correlation_id` on `EventMetadata`. No two-table query needed.
- Track A flag against Chapter 10's `ch10_processManager_code` and its surrounding prose, which describe PM persistence as a state-store pattern (`IProcessManagerStore`). Phase 17 reconciliation updates the depicted shape to event-sourced persistence with `AppendProcessManagerEventsAsync` and `ReadProcessManagerStreamAsync`. Chapter 10's pedagogy, the state-guard discipline, idempotency, timeouts, and compensation paths, survives the reconciliation unchanged.

## Trigger for revisiting

The same-table-no-outbox commitment is reversible. Conditions that would justify reopening it:

- PM events acquire external consumers: a future projection or bounded context legitimately needs to observe PM stream state directly, rather than deriving it from aggregate events. If that need arises, the outbox-bypass becomes an obstacle and the routing model has to widen.
- The operational cost of two registries and prefix-based routing exceeds the maintenance cost of a separate `process_manager_events` table. The current scale (two PM types, one event-store adapter today, several adapters in the Phase 13/14 roadmap) does not approach this threshold; future codebase expansion might.
