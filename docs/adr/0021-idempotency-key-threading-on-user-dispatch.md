# 0021. Idempotency-Key Threading on the User-Dispatch Path

## Status

Accepted (May 2026)

## Context

`ICommandContext` already carries a nullable `IdempotencyKey` (ADR 0014), and
`IdempotencyBehavior<TCommand>` reads it off `ICommandContextAccessor` to dedupe
a command that arrives twice (ADR 0016). The behavior passes through on a null
or blank key, so deduplication is opt-in by whoever dispatches the command.

Today only one originator can opt in. `CausedCommandBus` populates the key for
process-manager dispatch through the internal `SendWithContextAsync` seam; the
user-dispatch path leaves it null. ADR 0016 said as much and deferred the rest:
"the HTTP edge that reads and enforces it is Phase 7 work." Phase 7 is that
work. The Api host's `POST /commands` endpoint reads a client-generated key from
the `Idempotency-Key` header, and a Web button-click generates one at user-intent
time. Both need that key to reach the command context so a double-submit or a
client retry dedupes through the behavior that already exists.

The user-dispatch path has no place to put the key. `ICommandBus.SendAsync(ICommand,
CancellationToken)` calls a private `SendInternal(command, correlationId, ct)`,
which is a recipe lambda feeding the shared `DispatchAsync` loop. The recipe
mints `CorrelationId`, `CausationCommandId = Guid.NewGuid()`, `ActorId =
Guid.Empty`, and `ServiceName` from options, and never sets `IdempotencyKey`, so
it defaults null. `DispatchAsync` opens the scope, builds the context from the
recipe, pushes the accessor, runs the pipeline, and restores in `finally`. There
is no parameter on this path through which a key can travel.

The process-manager path does have a seam that accepts caller-supplied context,
`SendWithContextAsync(ICommand, CausedDispatchFragment, ct)`, and the fragment
carries an `IdempotencyKey`. But that seam exists for a different intent: the
caller supplies every context value (`CorrelationId`, `CausationCommandId`,
`ActorId`, `ServiceName`) read from the causing event and the actor, instead of
letting the bus mint them (ADR 0014). The user-with-key case is not that. It
mints fresh correlation, causation, actor, and service-name values exactly like
the bare user path, and adds only a key. Reusing the PM seam for it would
re-express the user-path minting at a second site, outside the dispatch scope,
duplicating logic that `SendInternal`'s own comment flags as a change point for
Phase 7 (the `ActorId = Guid.Empty` line, once authentication maps a principal).

The constraint: give the user-dispatch path a way to supply an idempotency key
while the rest of the context stays minted in the one place it is minted today.

## Decision

`ICommandBus` gains a public overload:

```csharp
Task SendAsync(ICommand command, string? idempotencyKey, CancellationToken ct);
```

This deliberately widens the interface past the bare-`SendAsync` shape Chapter 10
depicts. The concrete `CommandBus`'s correlation-id overload was kept off the
interface on purpose, to hold that shape; this overload goes on it for a reason
that the correlation case did not have. The Phase 7 Api host resolves
`ICommandBus` through DI and dispatches every user command through it, and
carrying an idempotency key is part of that user-dispatch contract for every
command, not a narrow edge concern. The host should depend on the public
dispatch interface, not reach for the concrete `CommandBus` (which
`CausedCommandBus` takes only because the internal seam lives in the same
assembly). The correlation-forwarding case stayed concrete-only because its
callers are narrow and already hold the concrete type or push a pre-built
context; the key-threading case is the public contract, so it belongs on the
interface.

The mechanism threads the key through the existing user-dispatch recipe rather
than the PM seam. `SendInternal` widens by one parameter:

```csharp
private Task SendInternal(
    ICommand command, Guid correlationId, string? idempotencyKey, CancellationToken ct)
```

Its recipe lambda sets `IdempotencyKey = idempotencyKey` alongside the fields it
already mints. The new public overload calls `SendInternal(command,
Guid.NewGuid(), idempotencyKey, ct)`. The two existing callers of `SendInternal`,
the bare `SendAsync` overload and the concrete-only correlation-id overload, pass
`null`, so their behavior is unchanged. The shared `DispatchAsync` loop, the
`AsyncLocal` accessor, and the pipeline are untouched. The key flows into
`CommandContext.IdempotencyKey`, and `IdempotencyBehavior<TCommand>` reads it off
the accessor the same way it reads a PM-supplied key.

A null key leaves the user path behaving exactly as the bare overload does:
`IdempotencyBehavior<TCommand>` passes through on null or blank, so
`SendAsync(command, null, ct)` is indistinguishable in effect from
`SendAsync(command, ct)`.

`SendWithContextAsync` and `CausedDispatchFragment` are not touched. Keeping the
seam process-manager-only holds the one-minting-site property: the user-path
context is built in exactly one recipe, and this overload adds a parameter to
that recipe instead of copying the minting elsewhere.

Alternatives rejected:

- Route the new overload through `SendWithContextAsync` with a fragment carrying
  the key, the rest minted (the seam reuse the planning draft first named). It is
  feasible, since `ApplicationOptions` is a singleton and resolves to the same
  instance off the root provider, but it re-expresses the user-path minting
  (`CausationCommandId`, `ActorId`, `ServiceName`) at a second site outside the
  dispatch scope. One minting site is worth more than one fewer parameter,
  especially with the `ActorId` line already marked as a Phase-7 change point.
- Add `IdempotencyKey` as a field on the command record, the shape Chapter 15
  shows. It puts an infrastructure concern on the command payload; the
  implementation keeps payload separate from dispatch context. Acceptable as
  chapter pedagogy, not as the implementation's shape. This is the F-0011
  candidate at the command-shape site.
- Push the key onto `ICommandContextAccessor` before calling the bare overload.
  It fails for the reason ADR 0014 documented: `DispatchAsync` builds its own
  `CommandContext` and overwrites the accessor, so the pushed value becomes the
  saved-previous one and the handler never sees the key.

## Consequences

- `ICommandBus` gains the public overload `SendAsync(ICommand, string?,
  CancellationToken)`. This is the first deliberate widening of the interface
  past the Chapter-10 bare-`SendAsync` shape; the justification is recorded above.
  F-0011 candidate against Chapter 10's `ICommandBus` shape, which depicts the
  interface with only the bare `SendAsync` overload. Session 0011 close
  transcribes. F-0011 candidate against Chapter 15's command-shape pedagogy,
  which threads the key on the command record where the implementation threads it
  as a dispatch-context parameter. Session 0011 close transcribes to the F-0011
  block; Phase 17 reconciles.
- `CommandBus` is the only implementer of `ICommandBus`, and no test double
  implements it, so the interface change compiles with one implementation edit
  (Commit 2). The two end-to-end tests that resolve `ICommandBus` call the bare
  overload and are unaffected.
- `SendInternal` widens by one parameter, `string? idempotencyKey`. Its two
  existing callers, the bare `SendAsync` overload and the concrete-only
  correlation-id overload, pass `null` and keep their current behavior exactly.
  The recipe sets `CommandContext.IdempotencyKey` from the parameter.
- `ICommandContext.IdempotencyKey` is already on disk (ADR 0014). This ADR does
  not add the field, the `CommandContext` property, or any storage. It adds only
  the public dispatch surface that lets a user-originated command populate the
  field the process-manager path already populates.
- `IdempotencyBehavior<TCommand>` is unchanged. It already reads the key off the
  accessor and passes through on null or blank, so a user-supplied key reaches it
  the same way a PM-supplied key does. The behavior order is unchanged:
  `LoggingCommandBehavior` outermost, `IdempotencyBehavior<TCommand>` inside it,
  `ValidationCommandBehavior` innermost.
- `SendWithContextAsync` and `CausedDispatchFragment` are unchanged, as is
  `CausedCommandBus`, the seam's one caller.
- Phase 7 consumes this. Cluster 2's `POST /commands` endpoint reads the
  `Idempotency-Key` header and threads it through the new overload; Cluster 4's
  `IApiClient.SendCommandAsync(command, idempotencyKey, ct)` takes the
  client-generated key, sets the header, and the Api host reads and threads it. A
  duplicate user submission carrying the same key dedupes through
  `IdempotencyBehavior<TCommand>` the way a redelivered PM command does.

## Trigger for revisiting

- A user-dispatched command returns data and a duplicate must return the same
  response. The no-op-success semantic (ADR 0016) fits void commands; a
  data-returning command needs the cached-response pattern ADR 0016 names in its
  own trigger section, which extends the table and the behavior, not this
  overload.
- A user-dispatch source needs to supply more context than a correlation id and
  an idempotency key while still minting the rest (a tenant id, say). A single
  `string?` parameter would not carry it; the path would take a small
  user-dispatch fragment of its own or widen the overload, and the
  one-minting-site reasoning here would be reweighed against the parameter count.
- Authentication lands and the user path maps an authenticated principal to
  `ActorId`. The recipe's `ActorId` minting changes in one place, the
  `SendInternal` recipe, and because the key path shares that recipe rather than
  copying it, the change does not ripple to a second site. That is the property
  this ADR's mechanism choice protects.
