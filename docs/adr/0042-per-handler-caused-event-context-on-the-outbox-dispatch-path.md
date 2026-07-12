# 0042. Per-Handler Caused-Event Context on the Outbox Dispatch Path

## Status

Accepted (July 2026). Fixes the process-manager metadata defect that broke ADR 0013's promise that PM rows join the aggregate trace by `correlation_id`. Applies ADR 0014's event-to-event causation shape to a process manager's own save, not just to the commands it dispatches.

## Context

A process manager writes its events through `ProcessManagerRepository.SaveAsync`, which stamps `EventMetadata` off the ambient `ICommandContextAccessor.Current`. When that accessor holds no context, the repository falls back to `BuildFallbackMetadata`, which writes `CorrelationId = Guid.Empty`, `CausationId = Guid.Empty`, `ActorId = Guid.Empty`, and `Source = "Workers"`.

`CommandBus` is the only writer of that accessor. A process manager driven by the outbox is not dispatched by `CommandBus`: `InProcessMessageDispatcher` resolves `IProcessManagerHandler<TEvent>` implementations and invokes them directly, setting only the tenant accessor. So on the outbox route, which is the normal route, every event a process manager writes carries an empty correlation, an empty causation, and no actor.

This was confirmed empirically against a Testcontainers PostgreSQL, driving real commands through the real bus and reading the rows back:

```
order:94c715…  | OrderPlaced                | 001192f5-03bf-4c18-affe-d8a5ead1c5f2
pm-order-fulfillment:94c715…  | OrderFulfillmentStarted    | 00000000-0000-0000-0000-000000000000
payment:80ed1a…  | PaymentAuthorized          | 001192f5-03bf-4c18-affe-d8a5ead1c5f2
pm-order-fulfillment:94c715…  | ReservationFailed          | 00000000-0000-0000-0000-000000000000
order:94c715…  | OrderCancelled             | 001192f5-03bf-4c18-affe-d8a5ead1c5f2
```

The aggregate side of the workflow is intact. The commands a process manager dispatches carry the correlation forward correctly, because `CausedCommandBus` reads it off the causing event's metadata rather than off the ambient accessor, and `CommandBus.SendWithContextAsync` then establishes a context for the aggregate write. It is only the process manager's own state stream that falls out of the trace.

The timeout route is unaffected. A due `delayed_commands` row resurfaces through `ICausedCommandBus` into `CommandBus.SendWithContextAsync`, so the handler runs inside a command pipeline and the accessor holds a real context when the PM saves. The consequence is that a single PM stream can hold events with a real correlation and events with an empty one, depending on which route drove the transition. Treating "PM rows" as one class with respect to correlation is wrong today.

ADR 0013's Consequences promise the opposite: "PM rows appear in the trace results alongside aggregate rows, joined by `correlation_id` on `EventMetadata`." The Phase 12 Correlation-ID Tracer is built on that promise. ADR 0014 makes no claim about PM saves; every correlation and causation claim in it is scoped to the commands a process manager dispatches, so it is accurate as written and needs no amendment.

`ProcessManagerRepository` already reads the accessor and already has a context-present branch. Nothing needs to change there. What is missing is a context on the outbox route.

## Decision

`InProcessMessageDispatcher` establishes a command context around each process-manager handler invocation, built from the causing event's metadata and that handler's declared actor. It captures the previous value, sets the new one, invokes the handler, and restores in a `finally`, the same discipline the method already applies to the tenant accessor. A nested command dispatch through `CausedCommandBus` pushes and restores its own context on top of this one, so the two nest correctly.

The context is established **per handler, not per message**. Two process managers can subscribe to the same event, and each writes under its own identity. A single context built once per message would have to pick one actor for the whole fan-out, which is wrong for whichever process manager did not get picked.

`CausedCommandContext` is a new sealed record in `Domain.Abstractions` implementing `ICommandContext`. It carries:

* `CorrelationId` from the causing event's `Metadata.CorrelationId`, so the process manager's events stay in the workflow the event belongs to.
* `CausationCommandId` from the causing event's `Metadata.EventId`. Causation is event-to-event, the shape ADR 0014 established for PM-dispatched commands, applied here to the process manager's own save.
* `ActorId` and `ServiceName` from the handler's declared `SystemActor`.
* `Roles` and `AuthorizationMode` matching what `CommandBus.SendWithContextAsync` hard-sets for system dispatch (`SystemActor.SystemRoles` and `DispatchAuthorizationMode.SystemActor`), so a process manager writing on the outbox route and one writing under a resurfaced timeout command carry the same authorization shape.
* `IdempotencyKey` null. An event dispatch carries no command key.
* `UtcNow()` off an injected `TimeProvider`, which the dispatcher resolves as `CommandBus` does, so both dispatch paths stamp `OccurredUtc` from one clock discipline.

`Domain.Abstractions` is the home because `Outbox` references it and nothing else. The Application internals stay internal, per ADR 0014's scoping.

`IProcessManagerHandler` gains a non-generic base declaring `SystemActor Actor { get; }`, which the generic `IProcessManagerHandler<TInboundEvent>` derives from. Both handlers already held their `SystemActors` identity as a private static field, so this is a promotion to a declared member, not a new identity. The base is non-generic because the dispatcher resolves handlers as `object` out of the container and would otherwise need reflection to reach the actor.

Scope is the process-manager loop. The `IEventHandler` loop in the same method is untouched: projections do not write events, so they have nothing to stamp.

## Rejected alternatives

**A single context per message, set once around both loops.** Cheaper, and it mirrors the tenant set-point exactly. Rejected because the actor is a per-handler value. The dispatcher fans one message out to every registered process-manager handler for that event type, and two distinct actors exist (`SystemActors.OrderFulfillment` and `SystemActors.Return`). A per-message context would stamp one PM's events with the other's identity as soon as both subscribe to one event. The correlation and causation would be right and the actor would be silently wrong, which is the worst shape of failure: a trace that looks complete and attributes the work to the wrong process manager.

**Threading the causing `EventMetadata` explicitly through `IProcessManagerRepository.SaveAsync`.** Explicit rather than ambient, which is usually the better instinct. Rejected on double mechanism: the repository would then have two ways to learn its metadata, the ambient accessor for the timeout route and a parameter for the outbox route, and every call site in both handlers would have to thread the value. The repository's context-present branch already works; the defect is that nothing establishes the context, not that the ambient mechanism is wrong.

**Each handler setting the accessor itself, at the top of `HandleAsync`.** Rejected on the failure mode. It is opt-in, so a new process manager that forgets the line silently writes empty correlation, and nothing fails. Putting it in the dispatcher makes it structural: a handler cannot be invoked on this path without a context, because the dispatcher establishes one before it calls.

**Widening `CommandBus.SendWithContextAsync` and `CausedDispatchFragment` to public, or adding an `Outbox` to `Application` project reference.** Either would let the dispatcher reuse the existing metadata-to-context translation. Rejected on ADR 0014's scoping, which put that seam internal deliberately and whose Revisit-when names cross-assembly visibility as the trigger to reopen it. That trigger has not fired: `Domain.Abstractions` is already visible to `Outbox`, so the context type lands there and the seam stays shut.

## Consequences

* Process-manager events written on the outbox route carry the causing workflow's correlation, an event-to-event causation, and the process manager's actor and service name. ADR 0013's Consequences hold from this commit forward.
* Five metadata fields change on those events, not one: `CorrelationId`, `CausationId`, `ActorId`, `Source`, and `OccurredUtc`, which now comes from the context's clock rather than the wall clock. No consumer reads any of them for control flow; the AdminConsole reads them for display only.
* The two routes converge. A process manager's events carry the same metadata shape whether an outbox dispatch or a resurfaced timeout drove the transition.
* `IProcessManagerHandler` is no longer a bare `HandleAsync` contract. Every process-manager handler now declares the identity it writes under. The DI registration walk filters forwardings on the generic interface, so the new base adds no registration.
* `InProcessMessageDispatcher` resolves `ICommandContextAccessor` only when a process manager is subscribed to the event it is dispatching. A host that dispatches events to projections alone, which is the AdminConsole's focused read composition under ADR 0040, composes no command-context accessor and needs none. A host that does run process managers and omits the accessor fails at resolution rather than writing empty metadata.

## No backfill

Events already written keep their empty correlation. Immutability holds: no migration rewrites `event_store.events`, and no compensating event is appended, because nothing about the business facts is wrong. Only the metadata that would have let an operator trace them is missing, and inventing it after the fact would be fabricating provenance the system never observed.

The epoch is this commit. A Correlation-ID Tracer querying a stream that spans it will show the process manager's pre-fix rows under the empty correlation and its post-fix rows under the workflow's. That discontinuity is a fact about the system's history and is reported as one, not smoothed over.

## Trigger for revisiting

The fallback in `ProcessManagerRepository.BuildFallbackMetadata` still exists and still silently stamps empty values whenever the accessor is null. With this commit, the only remaining callers reaching it are tests that construct the repository directly and seed process managers outside a dispatch. The next commit closes that hole by making a missing context fail closed rather than fall back, which is what turns this fix from "the live paths are correct" into "an incorrect path cannot compile or run". Until it lands, a future dispatch source that saves a process manager without establishing a context will reintroduce the defect silently.
