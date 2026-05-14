# Session 0005: Weeks 5-6 first projection and read side

Date: 2026-05-14
Phase: 6 (Weeks 11-12) per PLAN.md; Weeks 5-6 of the reconciled six-week foundation plan. The reconciled plan pulls the first projection forward so the Order workflow becomes runnable end-to-end before the other aggregates ship. PLAN.md reconciliation is owed in Phase 14 per F-0001-A.

## Status

In progress. Track C executing the seven-commit slice from `docs/sessions/0005-weeks5-6-first-projection-setup.md`.

1. `9361f27` — EventEnvelope.GlobalPosition and IEventStore.ReadAllAsync; PostgresEventStore and InMemoryEventStore read paths (6 new xUnit cases)

Commits 2-7 pending.

## Scope

The first projection and the read side: `EventEnvelope.GlobalPosition`, `IEventStore.ReadAllAsync`, the `EventContext<TEvent>` handler signature, `ICheckpointStore`, the `read_models` schema, `OrderListProjection` with its store, the `ProjectionReplayer`, and the `AddReadModels` composition root. The setup document carries the full design record and the per-commit slicing; this log records what shipped and where execution refined or deviated from that record.

Out of scope: LISTEN/NOTIFY dispatch (deferred to the Workers host or first UI session), the other three projections, per-event catch-up batching. The full deferred list lives in the setup document.

## Deviations from the design record

The setup document is the design record. A deviation here is a divergence from that record caught during execution. None so far has changed a design decision; the entries are scope resequencing and refinements the record was silent on or mildly wrong about.

**Commit 1: OutboxMessage.GlobalPosition and the OutboxProcessor query change resequenced from commit 1 to commit 2.** Setup-doc decision 2 asserted the outbox row already carried `global_position` ("the outbox processor's deserialization path already reads the column at zero additional cost"). Verification against `migrations/0001_initial_event_store.sql` showed it does not: `event_store.outbox` has no `global_position` column, and `OutboxProcessor.SelectPendingAsync` does not read one. Populating `OutboxMessage.GlobalPosition` therefore requires a sourcing decision, JOIN to `event_store.events` on `event_id` versus a new column on the outbox table populated at append time. That decision deserves the dispatcher-refactor context of commit 2, where the `EventContext<TEvent>` work lands and `OutboxProcessorTests` are touched anyway. An INNER JOIN would also have broken all eight `OutboxProcessorTests`, which seed outbox rows directly with no matching events row. This is a deviation from the design record's commit slicing, not from a design decision: `OutboxMessage` still gains the field and the live-tail path still carries `GlobalPosition`, one commit later than the record placed it. Commit 1 stayed read-side only and disturbed no Session 0004 test.

**Commit 1: Infrastructure.Tests gains 6 xUnit cases, not the 5 the setup-doc projection anticipated.** The setup-doc test-count table projected +5 for `PostgresEventStore.ReadAllAsync`. `InMemoryEventStore` also had to implement `ReadAllAsync` to satisfy the widened `IEventStore` contract, and "untested code is a defect" (CLAUDE.md) made one `InMemoryEventStore` `ReadAllAsync` test the honest minimum. The in-memory store now assigns `GlobalPosition` monotonically on append, mirroring PostgreSQL IDENTITY, which is behaviour worth one explicit test. Confirmed with the human before execution.

**Commit 1: PostgresEventStore.ReadAllAsync checks the cancellation token explicitly per row.** The setup record described the cancellation path as `[EnumeratorCancellation]` plus the token threaded into `ReadAsync`. In practice `NpgsqlDataReader.ReadAsync(ct)` only observes the token when it performs real async I/O; a small buffered result set sees no cancellation between rows, and `ReadAll_honors_cancellation_mid_enumeration` failed on the first run because of it. The iterator now calls `ct.ThrowIfCancellationRequested()` at the top of each row, matching what `InMemoryEventStore.ReadAllAsync` already does. This makes enumeration stop deterministically regardless of buffering.

## Test count

Legend per the Session 0004 convention: **planned methods** = setup-doc projection; **methods** = test methods Track C wrote; **xUnit cases** = test-runner total including `[Theory]` row expansion.

| Checkpoint | Domain.Tests | Infrastructure.Tests | Projections.Tests | Total |
| --- | --- | --- | --- | --- |
| Session 0004 actual xUnit cases | 22 | 55 | 0 | 77 |
| Commit 1 actual xUnit cases | 22 | 61 | 0 | 83 |

Commit 1 method-level: 5 new `PostgresEventStore_ReadAllAsync_Tests` methods, 1 new `InMemoryEventStoreTests` method, plus `GlobalPosition` assertions added to two existing read tests without adding methods.

`Infrastructure.Tests` warm-cache wall time stayed at ~3s; container reuse via `IClassFixture<PostgresFixture>` absorbed the new test class.

## Cross-track flags (Track A)

Carried from the setup document's pre-recorded flag candidates; final IDs assigned at session-log close. No execution-discovered flags yet beyond what the setup document anticipated.
