# 0014. CausedCommandBus for Process Managers

## Status

Accepted (May 2026)

## Context

`ICommandBus.SendAsync(ICommand, CancellationToken)` is the public command-dispatch surface. The bare overload supplies a fresh `CorrelationId`; `CommandBus.SendInternal` then mints the rest of the context (`CausationCommandId = Guid.NewGuid()`, `ActorId = Guid.Empty`, `ServiceName` from options), sets `ICommandContextAccessor.Current` to the new context, runs the pipeline, restores the previous accessor value in `finally`. The accessor pattern is `AsyncLocal`-backed (`AsyncLocalCommandContextAccessor`), registered as a singleton and widely injected. `CommandBus` already exposes a second overload `SendAsync(ICommand, Guid correlationId, CancellationToken)` whose comment names "a process manager continuing an existing causation chain" as the use case; the overload threads the correlation but still mints fresh causation and empty actor.

PM-originated dispatch needs three values propagated from the causing event into the new command's context: the `CorrelationId` (so the trace remains one workflow), the causing event's `EventId` (as the new command's causation source), and a PM-identifying actor (so audit logs and the Phase 12 Correlation-ID Tracer can attribute the command). The user-dispatch path mints fresh values for all three. The existing correlation-threading overload propagates the first but mints the second and third. Neither fits.

A "push context before calling `SendAsync`" wrapper around `ICommandBus` cannot work: `CommandBus.SendInternal` unconditionally builds its own `CommandContext` and overwrites `ICommandContextAccessor.Current`, so the pushed context becomes the saved-previous value and the handler sees the freshly-minted one. A parallel command dispatcher that duplicates `CommandBus`'s scope, pipeline-application, and accessor machinery would diverge from the user path on behavior (pipeline behaviors applied twice or not at all, accessor scope confused) and would double the maintenance surface for pipeline changes. The constraint is: PM-originated dispatch must share `CommandBus`'s pipeline and accessor mechanism while supplying the context values rather than minting them.

## Decision

`CommandBus` exposes a new internal entry point, `SendWithContextAsync(ICommand command, CausedDispatchFragment fragment, CancellationToken ct)`, that constructs `CommandContext` from a caller-supplied fragment instead of minting fresh values. Amended (P10.4): the seam gained a sibling `TenantId` parameter and now reads `SendWithContextAsync(ICommand command, CausedDispatchFragment fragment, TenantId tenant, CancellationToken ct)`, so a caused command runs in its causing event's tenant; the tenant rides as a sibling parameter on the seam and the fragment is unchanged. The fragment is a record carrying `CorrelationId`, `CausationCommandId`, `ActorId`, `ServiceName`, and `IdempotencyKey`. The seam runs the same accessor-set / pipeline-invoke / accessor-restore loop the user path runs, with the caller-supplied context in place of the minted one. The seam is `internal`; user-side dispatch through `ICommandBus.SendAsync` is unchanged.

`ICausedCommandBus` is the PM-facing dispatch surface:

```csharp
public interface ICausedCommandBus
{
    Task SendAsync(
        ICommand command,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string? idempotencyKey,
        CancellationToken ct);
}
```

The `EventMetadata` parameter dissolves the `EventContext<TEvent>` invariance question. PM handlers receive concrete-typed `EventContext<TInboundEvent>` per ADR 0010, and `context.Metadata` gives them an `EventMetadata` value that is invariant in event type. The bus reads `CorrelationId` and `EventId` from the metadata; it does not need the payload. `SystemActor` is a record carrying both a `Guid` actor identity and a `ServiceName` string; the bus sets `fragment.ActorId = actor.Id` and `fragment.ServiceName = actor.ServiceName` from it. `idempotencyKey` is nullable; PM handlers supply a key for the dispatches that need replay-idempotency. The implementation builds the `CausedDispatchFragment` (`CorrelationId = causingEventMetadata.CorrelationId`, `CausationCommandId = causingEventMetadata.EventId`, plus the actor fields and idempotency key) and calls the seam, passing the causing event's tenant (`causingEventMetadata.Tenant`) as the sibling `TenantId` parameter added at P10.4.

`ICommandContext` extends with `string? IdempotencyKey { get; }`, implemented on `CommandContext` as `public string? IdempotencyKey { get; init; }` (non-required, defaults to null). The existing `required` properties stay required; only the seam sets the new field.

Causation is modeled as event-to-event: a PM-dispatched command's `CausationCommandId` is set to the causing event's `EventId`. The field name (`CausationCommandId`) predates the PM use case; it was named for the user-dispatch path where causation does originate from a command. The PM use sets the field to an event identity. The dual semantic is documented here and not resolved by renaming; the field shape is unchanged.

PM-identifying actor identities live in a new `SystemActors` static class in `Domain.Abstractions`, holding `SystemActor` constants per PM. `SystemActor` itself is also defined in `Domain.Abstractions`. Constants-only, no behavior, no I/O. The placement is peer to `EventMetadata.ActorId` which already lives in `Domain.Abstractions`, and the layer is visible to Domain, Application, and Hosts uniformly. Human-readable PM identity (`"pm:order-fulfillment"`) lives in `SystemActor.ServiceName`, which the bus copies into the context.

## Consequences

- `CommandBus` gains an internal `SendWithContextAsync` seam that accepts a caller-supplied `CausedDispatchFragment`. Public `ICommandBus.SendAsync` is unchanged. The seam shares the accessor-set / pipeline-invoke / accessor-restore loop with the existing user-dispatch path; pipeline behaviors run once, the accessor scope holds correctly.
- `ICausedCommandBus` is the PM-side dispatch surface. PM handlers depend on it, not on `ICommandBus`. The type system enforces causation propagation at the PM dispatch site: a PM handler cannot reach a non-causation-aware bus without explicit cast or DI override.
- `ICommandContext` adds `string? IdempotencyKey`. The concrete `CommandContext` adds `public string? IdempotencyKey { get; init; }` non-required, defaulting null. Other `ICommandContext` implementers (test doubles, primarily) add the property; pre-flight in the foundational commit confirms the ripple scope before the change ships.
- `SystemActors` and `SystemActor` ship in `Domain.Abstractions`. Specific `Guid` values are stable, hand-generated constants; this ADR does not commit to particular values.
- PM-triggered command dispatches set `CausationCommandId = causingEventMetadata.EventId`. The Phase 12 Correlation-ID Tracer reads causation chains through the same field for both user-dispatched and PM-dispatched commands; the chain shape is event-to-event when the source is a PM, command-to-command-to-event when the source is a user.
- Track A flag against Chapter 10's `ch10_processManager_code`, which depicts PM dispatch as direct `ICommandBus.SendAsync` calls with idempotency keys threaded through the command record itself. Phase 17 reconciliation updates the depicted shape to `ICausedCommandBus` with explicit causing-metadata and actor parameters. Chapter 10's pedagogy, the state-guard discipline, idempotency, timeouts, and compensation paths, survives the reconciliation unchanged.

## Trigger for revisiting

The seam-and-wrapper structure is reversible. Conditions that would justify reopening it:

- A future dispatch source needs context propagation that the fragment shape does not carry. The current fragment carries the four context fields plus idempotency key; additional fields (distributed-trace span identifiers, tenant identifiers) would extend the fragment or trigger a different seam. This condition fired for the tenant case at P10.4 and resolved without reopening the seam-and-wrapper structure: the tenant rides as a sibling `TenantId` parameter on the existing seam, neither extending the fragment nor opening a different seam, because it is a dispatch-context value the seam carries alongside the fragment.
- The internal-visibility scope on `SendWithContextAsync` becomes a maintenance friction for legitimate cross-assembly uses. The current scope assumes one Application project owns `CausedCommandBus`; if PM-originated dispatch moves to a separate assembly, the visibility rule needs to widen.
