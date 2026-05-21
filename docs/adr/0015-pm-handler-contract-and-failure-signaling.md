# 0015. Process-Manager Handler Contract and Command-Failure Signaling

## Status

Accepted (May 2026)

## Context

Commit 10 shipped ICausedCommandBus, the dispatch surface a process manager
uses to issue commands (ADR 0014), and IProcessManagerHandler<TInboundEvent>,
the subscriber contract for the events a process manager reacts to. This ADR
documents the two surfaces and commits the failure-signaling shape that sits
between them.

Projections subscribe to events through IEventHandler<TEvent> (ADR 0010).
Process managers need a parallel contract rather than the same one because the
two consumer kinds have different lifecycles. A projection is idempotent by
construction and its failures are almost always transient. A process manager
loads state, mutates it, dispatches commands, and saves; some of its dispatch
failures are workflow signals it must act on, not transient faults to retry.

Phase 4 aggregates reject invalid commands by throwing: Inventory.Reserve
throws DomainException on insufficient stock, and any append that loses an
optimistic-concurrency race throws ConcurrencyException. A process manager
dispatching such a command needs the rejection as data it can branch on, not as
an unhandled exception that aborts the handler. The OrderFulfillmentProcessManager
dispatches inventory reservations as a parallel per-line fan-out; collecting a
complete set of per-line outcomes is what lets it run partial-failure
compensation, so a single rejected line cannot be allowed to abort the batch.

## Decision

IProcessManagerHandler<TInboundEvent> is the process-manager subscriber
contract, parallel to IEventHandler<TEvent> and sharing its
EventContext<TInboundEvent> signature (ADR 0010). The message dispatcher routes
by interface: an aggregate event reaches projection subscribers through their
IEventHandler<T> registrations and process-manager subscribers through their
IProcessManagerHandler<T> registrations, so the two keep separate failure
semantics over the shared polling-and-quarantine machinery. The routing
implementation lands with the first process-manager consumer; this ADR commits
the contract.

CommandOutcome is the dispatch-result discriminator: a success flag and an
optional captured failure, with Success() and Failed(Exception) factories.
Process-manager handlers switch on it instead of wrapping each dispatch in
try/catch.

TrySendAsync, an extension on ICausedCommandBus, converts the expected
dispatch-failure modes to CommandOutcome.Failed and lets unexpected faults
propagate. The expected modes are DomainException (an aggregate invariant
rejected the command) and ConcurrencyException (the command lost an
optimistic-concurrency race on the target stream). Everything else, a
serialization fault, a connection failure, a programming error, propagates, and
the outbox redelivers the triggering event so the whole handler retries.

The classification rule is the boundary, not the specific exception list. An
exception is expected when it is a dispatch outcome the process manager must see
as data to make an orchestration decision. It is unexpected when it is a fault
that warrants re-running the whole handler. The two named modes are exactly what
Phase 4 aggregates raise on command rejection. DomainException must be an
outcome because it will not clear on retry; propagating it would build a poison
message bound for quarantine. ConcurrencyException must be an outcome because
the parallel reservation fan-out dispatches through Task.WhenAll and needs every
dispatch to report rather than fail fast on the first conflict; the idempotency
keys (ADR 0016) keep re-dispatch of an already-applied command safe where the
handler chooses to retry.

Process managers may load aggregate state through IEventStoreRepository<TAggregate>
for orchestration decisions the events alone do not carry. This is a read-only
cross-aggregate access pattern. The first concrete use is the
OrderFulfillmentProcessManager loading Order.Lines for the per-line reservation
fan-out. The permission is recorded here ahead of that use; no code in this
commit reaches it.

## Consequences

- CommandOutcome and the TrySendAsync extension ship in Domain.Abstractions,
  alongside ICausedCommandBus, SystemActor, DomainException, and
  ConcurrencyException. The placement keeps them reachable from process-manager
  handlers wherever those land without forcing an Application dependency.
- Process-manager handlers depend on ICausedCommandBus and call TrySendAsync,
  switching on CommandOutcome. They do not catch exceptions in handler bodies.
- A parallel fan-out runs Task.WhenAll over several TrySendAsync calls and gets
  a complete outcome set even when some dispatches are rejected, because
  TrySendAsync does not throw for the expected modes.
- The dispatcher routes by handler interface so projection and process-manager
  subscribers to the same event keep distinct failure handling. The routing
  lands with the first process-manager consumer commit.
- Cross-aggregate reads from a process manager are read-only, bounded by the
  aggregate's event count, and concentrated in process-manager code rather than
  smeared across aggregates.
- The atomicity boundary is the state-guard pattern, not a transaction.
  Load-dispatch-save are three operations; the state guard makes a re-delivered
  event a no-op, so a crash between dispatch and save is recoverable by
  redelivery rather than by wrapping the three in a single transaction, which
  the dispatch step would force to span the bus and the other aggregates'
  repositories it calls into.

## Trigger for revisiting

- A future aggregate throws a third exception type that is a workflow signal
  rather than a fault. The TrySendAsync catch set widens, and the
  expected-versus-unexpected rule named above is the test for inclusion.
- ConcurrencyException turns out, in practice, to want whole-handler retry
  rather than a Failed outcome. The classification moves it to the propagating
  set, and the fan-out collects partial outcomes by some other means (sequential
  dispatch, or a WhenAll shape that captures exceptions rather than propagating
  them). This is the residual tension in the current decision: a concurrency
  conflict is treated as an outcome to keep fan-out complete, but it is the one
  expected mode that is also plausibly transient.
- The state-guard-only atomicity proves insufficient because a failure mode
  slips between dispatch and save that the guard does not catch. Explicit
  transaction or saga-log wrapping gets reconsidered.
