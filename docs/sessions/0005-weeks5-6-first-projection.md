# Session 0005: Weeks 5-6 first projection and read side

Date: 2026-05-14
Phase: 6 (Weeks 11-12) per PLAN.md; Weeks 5-6 of the reconciled six-week foundation plan. The reconciled plan pulls the first projection forward so the Order workflow becomes runnable end-to-end before the other aggregates ship. PLAN.md reconciliation is owed in Phase 14 per F-0001-A.

## Status

In progress. Track C executing the commit slice from `docs/sessions/0005-weeks5-6-first-projection-setup.md`. The setup document's seven-commit slice is now six: commit 3 absorbs the standalone migration commit (see the commit-3 deviations).

1. `9361f27` — EventEnvelope.GlobalPosition and IEventStore.ReadAllAsync; PostgresEventStore and InMemoryEventStore read paths (6 new xUnit cases)
2. `5989ebe` — EventContext, IEventHandler widening, OutboxMessage.GlobalPosition, migration 0002, outbox global_position sourcing (2 new xUnit cases)
3. `1cc073e` — ICheckpointStore, ReadModels.Postgres project, PostgresCheckpointStore, migration 0003, Projections.Tests project (4 new xUnit cases)

Commits 4-6 pending.

## Scope

The first projection and the read side: `EventEnvelope.GlobalPosition`, `IEventStore.ReadAllAsync`, the `EventContext<TEvent>` handler signature, `ICheckpointStore`, the `read_models` schema, `OrderListProjection` with its store, the `ProjectionReplayer`, and the `AddReadModels` composition root. The setup document carries the full design record and the per-commit slicing; this log records what shipped and where execution refined or deviated from that record.

Out of scope: LISTEN/NOTIFY dispatch (deferred to the Workers host or first UI session), the other three projections, per-event catch-up batching. The full deferred list lives in the setup document.

## Deviations from the design record

The setup document is the design record. A deviation here is a divergence from that record caught during execution. None so far has changed a design decision; the entries are scope resequencing and refinements the record was silent on or mildly wrong about.

**Commit 1: OutboxMessage.GlobalPosition and the OutboxProcessor query change resequenced from commit 1 to commit 2.** Setup-doc decision 2 asserted the outbox row already carried `global_position` ("the outbox processor's deserialization path already reads the column at zero additional cost"). Verification against `migrations/0001_initial_event_store.sql` showed it does not: `event_store.outbox` has no `global_position` column, and `OutboxProcessor.SelectPendingAsync` does not read one. Populating `OutboxMessage.GlobalPosition` therefore requires a sourcing decision, JOIN to `event_store.events` on `event_id` versus a new column on the outbox table populated at append time. That decision deserves the dispatcher-refactor context of commit 2, where the `EventContext<TEvent>` work lands and `OutboxProcessorTests` are touched anyway. An INNER JOIN would also have broken all eight `OutboxProcessorTests`, which seed outbox rows directly with no matching events row. This is a deviation from the design record's commit slicing, not from a design decision: `OutboxMessage` still gains the field and the live-tail path still carries `GlobalPosition`, one commit later than the record placed it. Commit 1 stayed read-side only and disturbed no Session 0004 test.

**Commit 1: Infrastructure.Tests gains 6 xUnit cases, not the 5 the setup-doc projection anticipated.** The setup-doc test-count table projected +5 for `PostgresEventStore.ReadAllAsync`. `InMemoryEventStore` also had to implement `ReadAllAsync` to satisfy the widened `IEventStore` contract, and "untested code is a defect" (CLAUDE.md) made one `InMemoryEventStore` `ReadAllAsync` test the honest minimum. The in-memory store now assigns `GlobalPosition` monotonically on append, mirroring PostgreSQL IDENTITY, which is behaviour worth one explicit test. Confirmed with the human before execution.

**Commit 1: PostgresEventStore.ReadAllAsync checks the cancellation token explicitly per row.** The setup record described the cancellation path as `[EnumeratorCancellation]` plus the token threaded into `ReadAsync`. In practice `NpgsqlDataReader.ReadAsync(ct)` only observes the token when it performs real async I/O; a small buffered result set sees no cancellation between rows, and `ReadAll_honors_cancellation_mid_enumeration` failed on the first run because of it. The iterator now calls `ct.ThrowIfCancellationRequested()` at the top of each row, matching what `InMemoryEventStore.ReadAllAsync` already does. This makes enumeration stop deterministically regardless of buffering.

**Commit 2: setup-doc commits 1 and 2 merged into one logical unit.** The setup document slices the `IEventHandler` widening (commit 2) separately from `OutboxMessage.GlobalPosition` (commit 1). Commit 1's resequencing pulled the outbox work forward, and the two pieces cannot split: `InProcessMessageDispatcher` builds `EventContext<TEvent>` from `OutboxMessage.GlobalPosition`, so the field and the signature widening land together. The session still runs seven commits; commit 2 is fatter, commits 3-7 are unchanged.

**Commit 2: `IEventHandler<TEvent>` ships invariant, not `<in TEvent>`.** Setup-doc decision 3 specified `IEventHandler<in TEvent>`, and Session 0004 shipped that contract. It does not compile once `HandleAsync` takes `EventContext<TEvent>`: `EventContext<>`'s positional `Event` property is init-settable, making `EventContext<>` invariant in its own type parameter, and the compiler rejects passing a contravariant `in TEvent` to an invariant generic (CS1961). Resolution: drop `in`, accept invariance. Functionally equivalent here because `InProcessMessageDispatcher` always resolves the exact closed handler type via `MakeGenericType(eventType)` plus `GetServices`, never a base type. The `IEventHandler.cs` file carries a comment recording the compiler reason so a future reader does not try to "fix" the missing modifier.

**Commit 2: migration numbering shifts the read-models migration to 0003.** `migrations/0002_add_outbox_global_position.sql` lands in this commit (the new-column sourcing path for `global_position` on the outbox, chosen over a JOIN: the outbox is already a denormalized dispatch envelope, so the column joins that set and keeps the row self-describing; it also leaves the `FOR UPDATE SKIP LOCKED` query and the `OutboxProcessorTests` isolation untouched). The setup document's commit 4 names the read-models migration `0002_initial_read_models.sql`; it becomes `0003_initial_read_models.sql`. Commit 4's session-log entry picks up the downstream filename change. `MigrationRunner` itself takes no code change, it is resource-name-driven. Four of the five `PostgresMigrationRunnerTests` hard-assert "1 migration" and took count bumps plus the `0002` row assertion in this commit; they take the same mechanical bump again in commit 4 when `0003` lands.

**Commit 3: scope absorbed the ReadModels.Postgres project, the connection factory, the migration, and Projections.Tests.** The setup document framed commit 3 as `ICheckpointStore` plus `PostgresCheckpointStore` only, with the `ReadModels.Postgres` project created in commit 5 and the read-models migration in commit 4. The checkpoint store cannot exist or be tested without all of it: a project to live in, an `IReadModelConnectionFactory` to open connections through, the `read_models.projection_checkpoints` table, and a test project. Commit 3 delivers the lot, plus the `.slnx` entries for both new projects.

**Commit 3: the read-models migration split (Alternative C), and the seven-commit slice became six.** Setup-doc decision 12 specified one `0002_initial_read_models.sql` creating the `read_models` schema, `projection_checkpoints`, `order_list`, and the `order_list` indexes, landing as a standalone migration commit (setup-doc commit 4). Shipping `order_list` now would front-run commit 4's design: its columns, the Money representation, nullability, and what the two indexes index are all `OrderListProjection` design that `OrderListRow` and `IOrderListStore` have not shaped yet. The migration splits into two complete, immutable files: `0003_initial_read_models.sql` here (schema plus `projection_checkpoints`), and `0004_*` in the order-list commit (the `order_list` table, designed with its C# consumer). Neither file is ever edited, so checksum discipline holds. The standalone migration commit dissolves; its content is split between commit 3 and the order-list commit. The session is now six commits.

**Commit 3: Projections.Tests references EventStore.Postgres for MigrationRunner, the third consumer.** The duplicated `PostgresFixture` calls `MigrationRunner`, which lives in `EventStore.Postgres`. The setup document's deferred-items list (item 5) set the trigger for extracting `MigrationRunner` to its own project at "when a third consumer surfaces." `EventStore.Postgres.Cli` and `Infrastructure.Tests` were the first two; `Projections.Tests` is the third, and the trigger fired here, earlier than the deferred-items list anticipated. Forward flag only: commit 3 just adds the project reference, the same dependency `Infrastructure.Tests` already carries. The extraction itself is not this session's work.

**Commit 3: the PostgresMigrationRunnerTests name reshape carries forward to commit 4.** `First_run_applies_initial_migration_to_empty_database` asserts three migrations as of this commit; the name has been loose since commit 2's second migration. Keeping it loose through commits 2 and 3 was deliberate. Commit 4 absorbs the standalone migration commit's role and brings the fourth migration (`0004`), which is the third count bump and the point where the diff justifies renaming the test to something like `applies_all_migrations_in_order`.

## Internal notes

**`NpgsqlConnectionFactory` takes an `NpgsqlDataSource`, not `PostgresEventStoreOptions`.** The Session 0005 commit-3 prompt framed the read-model connection factory as "reading `ReadModelOptions.ConnectionString`." It does not, and neither does the event-store factory it mirrors. `NpgsqlConnectionFactory` takes an `NpgsqlDataSource`; the options class is read by the DI extension (`AddPostgresEventStore`) to build that data source. `NpgsqlReadModelConnectionFactory` mirrors this exactly: it takes an `NpgsqlDataSource`, and `ReadModelOptions` (deferred to commit 6) is read by `AddReadModels`. Recorded so a future reader does not reconstruct the wrong wiring from the prompt's framing.

## Test count

Legend per the Session 0004 convention: **planned methods** = setup-doc projection; **methods** = test methods Track C wrote; **xUnit cases** = test-runner total including `[Theory]` row expansion.

| Checkpoint | Domain.Tests | Infrastructure.Tests | Projections.Tests | Total |
| --- | --- | --- | --- | --- |
| Session 0004 actual xUnit cases | 22 | 55 | 0 | 77 |
| Commit 1 actual xUnit cases | 22 | 61 | 0 | 83 |
| Commit 2 actual xUnit cases | 22 | 63 | 0 | 85 |
| Commit 3 actual xUnit cases | 22 | 63 | 4 | 89 |

Commit 1 method-level: 5 new `PostgresEventStore_ReadAllAsync_Tests` methods, 1 new `InMemoryEventStoreTests` method, plus `GlobalPosition` assertions added to two existing read tests without adding methods.

Commit 2 method-level: 2 new `InProcessMessageDispatcherTests` methods. The `OutboxProcessorTests`, `PostgresMigrationRunnerTests`, and `PostgresEventStore_AppendAsync_Tests` updates are assertion-level and helper-level, no method-count change. The 2 new methods exceed the setup-doc commit-2 projection of zero, justified the same way as commit 1's `InMemoryEventStore` test: the reflection that builds a closed `EventContext<>` is the riskiest new code in the commit and otherwise has no unit coverage until commit 6's rebuild test exercises it as a black box.

Commit 3 method-level: 4 new `PostgresCheckpointStoreTests` methods in the new `Projections.Tests` project. The `PostgresMigrationRunnerTests` updates for migration `0003` are assertion-level, no method-count change, so `Infrastructure.Tests` stays at 63.

`Infrastructure.Tests` warm-cache wall time stayed at ~3s; container reuse via `IClassFixture<PostgresFixture>` absorbed the new test class.

## Cross-track flags (Track A)

Carried from the setup document's pre-recorded flag candidates; final IDs assigned at session-log close. No execution-discovered flags yet beyond what the setup document anticipated.
