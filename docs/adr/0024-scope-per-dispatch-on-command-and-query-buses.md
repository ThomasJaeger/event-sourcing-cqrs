# 0024. Scope-per-dispatch on command and query buses

## Status

Accepted (May 2026)

## Context

`CommandBus` and `QueryBus` are singleton services holding the root
`IServiceProvider`. Their handlers (`ICommandHandler<TCommand>`,
`IQueryHandler<TQuery, TResult>`) register with `AddScoped` so that
each dispatch resolves a fresh handler instance and so that any scoped
dependencies the handlers hold (read-model stores in hosts that
register them scoped, unit-of-work-style resources) get fresh
instances per dispatch as well.

`ICommandHandler` and `IQueryHandler` are themselves non-disposable;
the scope exists for dependency-resolution and scoped-dependency
disposal, not for handler-lifetime management.

A singleton holding the root provider cannot resolve a scoped service
from the root: ASP.NET Core's default validating provider (which
`WebApplicationFactory<TProgram>` enables for tests) throws
`InvalidOperationException` on the resolution attempt. The validation
exists precisely to catch the lifetime mismatch that would otherwise
silently produce captive-dependency bugs in production.

Through Phase 6, `CommandBus.DispatchAsync` already opened a scope per
dispatch and resolved handler-and-behaviors from the scope, awaiting
the pipeline inside the scope's `using` block so the scope survives
until pipeline completion. The pattern was introduced incidentally
with the bus's original implementation and was not documented as an
architectural decision; it lived as an implementation detail.

`QueryBus.AskAsync` did not adopt the pattern, because no consumer
through Phase 6 exercised it under a validating provider. The
`Application.Tests` query tests construct handlers directly with stub
stores, and the in-process query path through `IQueryBus` had no
ASP.NET-side consumer. The Phase 7 `POST /queries` endpoint (Commit 14
of this cluster) became the first such consumer, and the validating
provider that `WebApplicationFactory<Program>` enables surfaced the
gap as a 500 on every dispatching test.

The fix is structurally trivial: mirror `CommandBus`'s scope handling
in `QueryBus`. The decision worth recording is the rule that prompts
the fix and that should hold for any future bus or dispatcher this
codebase introduces.

## Decision

Any singleton dispatcher resolving scoped handlers opens a DI scope
per dispatch, resolves handler-and-pipeline-behaviors from the scope's
`ServiceProvider`, and awaits the pipeline inside the scope's `using`
block. The dispatch method is `async` because returning the pipeline
task directly would dispose the scope before the pipeline runs.

The pattern applies to both `CommandBus.DispatchAsync` (already
established) and `QueryBus.AskAsync` (adopted in the commit that
lands this ADR). Future dispatchers (the Phase 9 caused-command-bus
adapter; any cross-context messaging dispatcher) follow the same
shape.

Handler interfaces (`ICommandHandler`, `IQueryHandler`) do not declare
`IDisposable`. The scope's role is to resolve scoped dependencies the
handler depends on and to dispose them at scope-end; the handler
itself is a transient artifact of the scope.

`ICommandBus` and `IQueryBus` interfaces do not change. The scope is
an implementation concern of the concrete bus.

## Consequences

- `QueryBus.AskAsync` becomes `async Task<TResult>` and adopts the
  `using var scope = _services.CreateScope()` plus `await pipeline()`
  shape that `CommandBus.DispatchAsync` already uses.
- Both `CommandBusTests` and `QueryBusTests` get a regression test
  using `ServiceProviderOptions { ValidateScopes = true }` and a
  scoped handler registration, asserting the bus dispatches without
  throwing. The tests are the executable form of this ADR's rule on
  both buses.
- Any future singleton dispatcher resolving scoped handlers must
  follow the same pattern; the regression-test shape transfers.
- No public interface changes. No host-composition changes. No
  migration. The fix lands as one method modification plus two test
  additions plus this ADR.
- F-0011 candidate against Chapter 15's query-dispatch depiction:
  the chapter does not show scope-per-dispatch on the query bus.
  The fix and this ADR establish the production shape; Phase 17
  reconciliation surfaces the depiction.

## Trigger for revisiting

The rule rests on handler-registration lifetime. Conditions that would
warrant reopening:

- Handlers registering at a different lifetime than scoped. If
  handlers ever register transient (per-resolution, no scope-end
  disposal) or singleton (no scope at all), the scope-per-dispatch
  pattern becomes either unnecessary or incorrect for those buses.
- A dispatcher that needs context to span multiple dispatches inside
  one logical operation (a batch dispatcher, or a saga coordinator
  not yet on the roadmap). Such a dispatcher would hold one scope
  across several pipeline invocations, which the current per-dispatch
  pattern does not anticipate.
- Performance evidence that scope-per-dispatch creates contention.
  None observed; the scope creation is cheap and the dispatch volume
  per host is modest. Recorded here as a non-trigger absent
  measurement.
