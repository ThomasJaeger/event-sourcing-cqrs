# 0010. Consumer-Side Event Handler Dispatch Shape

## Status

Accepted (May 2026)

## Context

Session 0005 shipped `IEventHandler<TEvent>` with the signature `HandleAsync(EventContext<TEvent> context, CancellationToken ct)`. `OrderListProjection` consumes it today across `OrderPlaced`, `OrderShipped`, and `OrderCancelled`. The `EventContext<TEvent>` record carries both the event payload and its envelope metadata (stream position, timestamps, correlation and causation), so a handler reads everything it needs from one argument.

`IEventHandler<TEvent>` is invariant in `TEvent`. Session 0005 dropped the `in` variance deliberately. The on-disk comment records the reason: `EventContext<TEvent>`'s positional `Event` property is init-settable, which is incompatible with contravariance. ADR 0010's single-convention commitment inherits that constraint.

Phase 5 introduces a second event-consumer family, process managers. ADR 0015 will define the `IProcessManagerHandler<TInboundEvent>` contract for that family. The open question is whether PM handlers reuse the `EventContext<TEvent>` signature or adopt Chapter 10's depicted three-argument shape `HandleAsync(TEvent evt, EventMetadata meta, CancellationToken ct)`. The metadata the three-argument shape passes separately is already reachable through `EventContext`, so the two shapes carry the same information.

## Decision

One consumer-side dispatch convention across the codebase. Event consumers, projections and process managers alike, handle events through `HandleAsync(EventContext<TEvent> context, CancellationToken ct)`. ADR 0015 names the interface that carries this signature for PM-bound subscribers. That interface is invariant in its inbound-event type parameter for the same reason `IEventHandler<TEvent>` is.

## Consequences

- `IProcessManagerHandler<TInboundEvent>` (defined by ADR 0015) carries the `EventContext<TEvent>` signature. PM handlers and projection handlers are written and read the same way.
- Consumer tests share one shape. Projection tests today instantiate the handler against an in-memory store double, build an `EventContext<T>`, and call `HandleAsync` directly. No named harness type exists. PM tests follow the same construct-context-and-invoke shape; commit 21 introduces a PM test helper for the multi-event process-manager flow.
- Handler discovery is shared. Session 0006's reflection-walk registration covers both `IEventHandler<EventContext<T>>` and the forthcoming `IProcessManagerHandler<T>` registrations from one mechanism.
- Track A flag against Chapter 10's `ch10_processManager_code` and `ch10_testing_code`, which depict the three-argument shape. Phase 14 reconciliation normalizes the signatures in those code blocks.
- Chapter 10's pedagogy, the state-guard discipline, idempotency, timeouts, and compensation paths, is unaffected by the signature change. The reconciliation touches signatures, not teaching.

## Trigger for revisiting

The single-convention commitment is reversible. Conditions that would justify reopening it:

- A future event-consumer family needs metadata that `EventContext<TEvent>` does not carry, and widening the context to serve it would burden the projection and PM consumers that do not need it.
- The shared signature couples projections and process managers tightly enough that one cannot change its dispatch shape without forcing a change on the other, and that coupling cost exceeds the single-convention benefit.
