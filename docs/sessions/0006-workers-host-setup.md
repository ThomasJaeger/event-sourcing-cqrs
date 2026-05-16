# Session Setup: Session 0006 — Workers host

This document captures the state of the build at the close of Session 0005 and the design decisions settled in the Session 0006 planning conversation. It bridges the planner conversation to the next code-repo Claude Code execution session.

Save this version as `docs/sessions/0006-workers-host-setup.md` after the next session begins. The post-execution session log lands at `docs/sessions/0006-workers-host.md` per the two-file convention from Session 0002 (`-setup.md` for planning input, no suffix for session log).

Methodology change versus prior setup documents: the planning conversation verified actual source state via read-only one-shot queries to the code-repo Claude Code before committing to each commit's design. Seven verification rounds, one per commit's surface area, caught five structural assumptions the planner would otherwise have shipped wrong. Each "Decisions settled" entry below carries an "Evidence" line citing the verification that grounded it.

## The reference implementation, current state

Public repo at github.com/ThomasJaeger/event-sourcing-cqrs. MIT licensed. .NET 10 LTS, C# 14, xUnit v2, FluentAssertions v7, Testcontainers for PostgreSQL integration. CI green on every push.

Phase 1 (Weeks 1-2 in-memory foundation), Phase 2's three PostgreSQL sessions (Weeks 3-4, Sessions 0002 through 0004), and Phase 6 first-projection work pulled forward into Weeks 5-6 (Session 0005) are complete. Session 0006 is the Workers host, which consumes the foundation end to end. PLAN.md numbers this work between Phase 6 and Phase 7; the reconciled six-week foundation plan from May 8 lands it as the closing piece of the runnable-Order-workflow window. PLAN.md reconciliation is owed in Phase 14 per F-0001-A.

## Where the build stands at the end of Session 0005

After Session 0005:

- `EventStore.Postgres` ships the full Phase 2 PostgreSQL slice plus the read-side widening: schema migrations via `MigrationRunner`, `PostgresEventStore` implementing `IEventStore` with atomic events-plus-outbox writes, `ReadAllAsync` streaming global ordering, `OutboxProcessor` draining the outbox by polling with backoff and quarantine, `InProcessMessageDispatcher` resolving `IEventHandler<TEvent>` from DI with `EventContext<TEvent>` envelopes, `AddPostgresEventStore` composition root, and migrations 0001 through 0004.
- `Domain.Abstractions` carries the cross-cutting ports: `AggregateRoot`, `IDomainEvent`, `IEventStore`, `IEventStoreRepository<TAggregate>`, `EventEnvelope` with `GlobalPosition`, `EventMetadata`, `EventContext<TEvent>`, `ConcurrencyException`, `DomainException`, `IMessageDispatcher`, `OutboxMessage`, `IEventHandler<TEvent>`, `IProjection`, `ICheckpointStore`, `StreamNames`.
- `Projections` ships `OrderListProjection` (handles `OrderPlaced`, `OrderShipped`, `OrderCancelled` against `IOrderListStore` with an `IOrderListUnitOfWork` write path), `ProjectionReplayer` for cold catch-up, and the per-projection store seam.
- `ReadModels.Postgres` ships `PostgresOrderListStore` with `PostgresOrderListUnitOfWork`, `PostgresCheckpointStore`, `NpgsqlReadModelConnectionFactory`, `IReadModelConnectionFactory`, and `AddReadModels`.
- The Order aggregate carries seven events across the cart-to-shipped lifecycle. Five other aggregates are deferred to Phase 4.
- 111 xUnit cases. 22 `Domain.Tests`, 63 `Infrastructure.Tests`, 26 `Projections.Tests`. All green. Warm-cache duration ~3s per test project that uses `PostgresFixture`.

What is not yet in place:

- No Workers host. `AddPostgresEventStore` registers `OutboxProcessor` as `AddHostedService<>` but no `Program.cs` runs it. `AddReadModels` is wired but unconsumed; `OrderListProjection`'s live tail never runs outside the `OrderListRebuildTests` rebuild test.
- No `LISTEN/NOTIFY` signaling. Dispatch is 500ms-idle-polling only.
- The two-phase `EventTypeRegistry` population (registry constructed empty by DI, host expected to call `Register<TEvent>()` per event type) has no production caller. Tests construct registries directly.
- `MigrationRunner` lives inside `EventStore.Postgres` with hardcoded assembly handle and resource prefix; three consumers reach into it today.
- `PostgresFixture` is duplicated between `Infrastructure.Tests` and `Projections.Tests`.
- The read-model `NpgsqlDataSource` is not disposed on container shutdown. The factory captures it; the factory is not disposable.

## Decisions locked across Sessions 0001-0005

From Session 0001:

- `IEventStoreRepository<TAggregate>`, not `IRepository<T>`. Chapter 8's repository shape.
- `AggregateRoot` as abstract base class, not interface.
- `EventEnvelope` is the C# boundary type, payload typed as `IDomainEvent`.
- In-memory event store is teaching scaffolding plus an `Application.Tests` fixture, not a fourth peer.
- `Money.Zero` carries currency-less identity. `DomainException` for business-rule violations.
- Test naming: snake_case sentence form.

From Session 0002:

- Event-store schema with `global_position BIGINT GENERATED ALWAYS AS IDENTITY` as PK on `event_store.events`, plus `(stream_id, stream_version)` UNIQUE for optimistic concurrency.
- Outbox shape: `event_store.outbox` and `event_store.outbox_quarantine`, partial index `ix_outbox_pending` ordering pending rows FIFO.
- Hand-rolled migrations applied by `MigrationRunner`, `pg_advisory_lock`-guarded, SHA-256 checksum verified.
- CLI via `EventStore.Postgres.Cli` with exit codes 0/1/64/78.
- Code-first vs manuscript routing rule: implementation choice wins, Track A flag logged.

From Session 0003:

- `ConcurrencyException` two-argument shape (`StreamId`, `ExpectedVersion`).
- `EventTypeRegistry` resolves logical event-type names to CLR types; lives in `Infrastructure/EventStore.Postgres` for v1.
- Snake-case JSON keys validated end to end.
- ADR 0004: self-contained event store adapters.

From Session 0004:

- `IMessageDispatcher` and `OutboxMessage` in `Domain.Abstractions`. `InProcessMessageDispatcher` lives in `src/Infrastructure/Outbox/`, engine-agnostic.
- `OutboxProcessor` polls every 500ms idle, 5s on exception. `FOR UPDATE SKIP LOCKED`, single transaction per batch, FIFO by `outbox_id`.
- Exponential backoff with full jitter, base 1s, cap 5min, `MaxAttempts = 10`.
- Quarantine via atomic CTE move with `DELETE ... RETURNING ... INSERT`.
- `AddPostgresEventStore` registers `OutboxProcessor` as `AddHostedService<>`.

From Session 0005:

- Projection live tail piggybacks on the outbox-dispatcher chain. Projections register `IEventHandler<TEvent>` per event type; `InProcessMessageDispatcher` fans out.
- `GlobalPosition` lives on `EventEnvelope`, `OutboxMessage`, and `EventContext<TEvent>`. `IEventHandler<TEvent>.HandleAsync(EventContext<TEvent>, CancellationToken)`. The interface is invariant, not `<in TEvent>` (compiler-driven; see Session 0005 commit-2 deviation).
- `ICheckpointStore` in `Domain.Abstractions` with `GetPositionAsync(name, ct)` and transaction-bound `AdvanceAsync(name, position, DbTransaction, ct)`. UPSERT-and-`GREATEST` idempotent advance.
- Read-model storage: two schemas in one PostgreSQL database for v1 (`event_store` and `read_models`), separation deferred.
- `IProjection` is marker-only (`Name` property). `ProjectionReplayer` constructor is `(IEventStore, IProjection)`, no DI dependency.
- `IOrderListStore.BeginAsync(ct)` returns `IOrderListUnitOfWork`; `CommitAsync(projectionName, position, ct)` advances checkpoint and commits in one transaction.
- `read_models` schema split across migrations 0003 (schema plus `projection_checkpoints`) and 0004 (`order_list` table plus indexes).
- F-0003-26 cluster scope narrowed via decisions 1, 4, 5, 6 in Session 0005's flag candidates list.

## Decisions settled in the Session 0006 planning conversation

These are the design choices the planner conversation closed before drafting this setup. Session 0006 implements them; deviations require a stop-and-surface step. Each entry's "Evidence" line cites the verification round (V1 through V7) that grounded it.

**1. Projection live tail uses the existing outbox-dispatcher chain.** The replayer runs once at startup via a new `ProjectionStartupCatchUpService : IHostedLifecycleService` to catch every registered `IProjection` up from its checkpoint to the current end of the events table. After `StartingAsync` returns, the `OutboxProcessor`'s `BackgroundService.StartAsync` runs and steady-state delivery flows through `InProcessMessageDispatcher` to the three `IEventHandler<TEvent>` registrations `AddReadModels` already wires. No parallel pipeline. The .NET host's lifecycle pipeline guarantees the `IHostedLifecycleService.StartingAsync` ordering. Evidence: V6 confirmed zero existing `IHostedLifecycleService` references and the `ProjectionReplayer(IEventStore, IProjection)` constructor shape.

**2. Handler-level idempotency closes the startup race.** A new `IOrderListUnitOfWork.GetCheckpointAsync(string projectionName, CancellationToken ct)` reads the projection's checkpoint inside the open transaction. `OrderListProjection.HandleAsync` calls it after `BeginAsync` and returns early (auto-rollback on `await using` dispose) if `context.GlobalPosition <= checkpoint`. Without this the `OrderPlaced` handler's INSERT on `(order_id)` PK conflicts on any re-delivery; the two UPDATE-only handlers are already idempotent at the SQL level. Defense-in-depth against the brief startup overlap and any future redelivery path. `ICheckpointStore` gains a transactional `GetPositionAsync(name, DbTransaction, ct)` overload; the existing non-transactional overload stays in place for the catch-up service's pre-replay read. Evidence: V1 confirmed the unit of work holds `_transaction` and `_checkpointStore` together, the interface is `IAsyncDisposable`, and `DisposeAsync` on the implementation will auto-rollback uncommitted transactions through `NpgsqlTransaction`'s contract.

**3. LISTEN/NOTIFY trigger and listener land in this session.** Migration `0005_add_outbox_notify_trigger.sql` adds an AFTER INSERT FOR EACH STATEMENT trigger on `event_store.outbox` whose function calls `pg_notify('outbox_pending', '')`. Statement-level (not row-level) so batched appends fire one notification per commit. `pg_notify` fires at COMMIT, so listeners only see notifications after the outbox row is visible — ordering falls out of the existing single-transaction `AppendAsync` shape. `OutboxProcessor` gains a `StartAsync` override that opens a long-lived listener `NpgsqlConnection` via `INpgsqlConnectionFactory`, runs `LISTEN @channel`, subscribes a `Notification` handler that signals a capacity-1 drop-write `Channel<bool>`, and starts a long-running task that loops `NpgsqlConnection.WaitAsync`. `StopAsync` cancels the listener cts and awaits the task. The idle wait in `ExecuteAsync` becomes `await Task.WhenAny(notificationTask, Task.Delay(IdlePollInterval, ...))`. Two new properties on `OutboxProcessorOptions`: `IdlePollInterval` (promoted from constant so listener tests can set it to a value high enough that timer-fallback is unambiguous) and `NotificationChannelName` (defaulted to `"outbox_pending"`). Evidence: V5 confirmed the single-transaction outbox-write shape, the per-batch short-lived connection pattern that the listener sits orthogonal to, the absence of any existing trigger on `event_store.outbox`, and that no existing `OutboxProcessorTests` exercise `ExecuteAsync` (all call `ProcessBatchAsync` directly).

**4. `MigrationRunner` extracts to a new `Infrastructure.Migrations.Postgres` project.** The runner has a hard Npgsql dependency, which makes the extraction engine-specific per ADR 0004's self-contained-adapters posture. The new project has one `PackageReference` (Npgsql) and zero `ProjectReference` entries. `MigrationRunner` gains a public `(Assembly assembly, string resourcePrefix)` constructor in place of the parameterless implicit one; both hardcoded values move to constructor parameters. A new static class `EventStorePostgresMigrations` in `EventStore.Postgres` owns the assembly handle and the resource-prefix string in one place. Call sites become `new MigrationRunner(EventStorePostgresMigrations.Assembly, EventStorePostgresMigrations.ResourcePrefix)`. Embedded `.sql` resources stay in `EventStore.Postgres`. Evidence: V3 confirmed the runner's parameterless construction at all four call sites (CLI, two `PostgresFixture` copies, five test methods in `PostgresMigrationRunnerTests`), no internal coupling to other `EventStore.Postgres` types beyond `MigrationFile` (a private nested record), and the embedded-resource declaration in the existing csproj.

**5. `EventTypeRegistry` per-bounded-context provider pattern.** New `IEventTypeProvider` in `Domain.Abstractions` with `IEnumerable<Type> GetEventTypes()`. Pull-shape, not push: the provider declares types, the registry consumes them. Keeps `Domain.Abstractions` independent of `EventTypeRegistry`'s location. New `SalesEventTypeProvider` in `src/Domain/Sales/` returns the seven Order events in canonical order. `EventTypeRegistry` gains non-generic `Register(Type)` and `Register(Type, string)` methods; the existing generic overloads delegate to them so the eight test call sites stay unchanged. `AddPostgresEventStore`'s registry registration becomes `TryAddSingleton<EventTypeRegistry>(sp => { ... })` with a factory that pulls every registered `IEventTypeProvider` and populates the registry on first resolution. Per-bounded-context granularity, not per-aggregate (Fulfillment in Phase 4 owns both Inventory and Shipment; one provider per context). Evidence: V4 confirmed the seven Order events live in `src/Domain/Sales/Events/`, the registry has no `Seal`/immutability mechanism, no production code calls `Register<T>()` today, and `Domain.Abstractions` is a leaf project that cannot reference `EventTypeRegistry`'s namespace without a layering inversion.

**6. `NpgsqlDataSource` disposal-lifetime via `NpgsqlReadModelConnectionFactory` implementing `IAsyncDisposable`.** The factory gains `IAsyncDisposable` on its declaration plus `public ValueTask DisposeAsync() => _dataSource.DisposeAsync();`. `IReadModelConnectionFactory` stays bare (disposal is an Npgsql-implementation detail, not a contract concern; test doubles handing out connections from a test-owned data source need no extra ceremony). `AddReadModels` stays unchanged: the container disposes the factory based on the concrete instance's interface, so the existing `TryAddSingleton<IReadModelConnectionFactory>` does the right thing once the concrete implements `IAsyncDisposable`. The event-store side already disposes correctly via its bare `TryAddSingleton<NpgsqlDataSource>` registration; the asymmetry between sides stays documented and deliberate, a consequence of Session 0005's commit-6 decision. Keyed-singleton symmetrization (`AddKeyedSingleton<NpgsqlDataSource>("event_store" / "read_models")`) is deferred unless the symmetry becomes structurally valuable. Evidence: V2 confirmed the factory holds `_dataSource` privately with no current disposal, the container has no other handle to the read-model data source, and `AddPostgresEventStore` registers `NpgsqlDataSource` as a bare container-tracked singleton.

**7. `PostgresFixture` extracts to `tests/TestInfrastructure/`.** Session 0005's deferred-items list set the extraction trigger at a third consumer. `Workers.Tests` is that third consumer. New `tests/TestInfrastructure/TestInfrastructure.csproj` referencing `Npgsql`, `Testcontainers.PostgreSql`, and the new `Infrastructure.Migrations.Postgres` from decision 4. The class moves; both existing copies replace with a `ProjectReference`. xUnit fixture scoping is per-assembly, so the three test projects still spin up three containers in parallel (no shared container across assemblies), but the implementation is in one place. The extraction is a standalone commit, so the diff stays readable and `dotnet build` stays green between steps. Evidence: V7 confirmed the existing `PostgresFixture` shape (per-class container via `IClassFixture<PostgresFixture>`, per-test fresh database via `CreateDatabaseAsync`), no shared-container mechanism today, and the Session 0005 comment in `Projections.Tests/PostgresFixture.cs` flagging this extraction.

**8. `WorkersHostFactory.Build(string eventStoreCs, string readModelCs)` for composition.** A static factory inside `src/Hosts/Workers/` constructs the host. `Program.cs` reads env vars, runs migrations, calls `WorkersHostFactory.Build`, awaits `host.RunAsync(cts.Token)`. The integration test calls the factory directly with testcontainer connection strings and drives `host.StartAsync`/`host.StopAsync` itself. Migration is `Program.cs` orchestration, not part of `Build`; the test fixture migrates ahead of the test, so the test path is migration-free. The factory wires `Host.CreateApplicationBuilder()`, registers `SalesEventTypeProvider`, calls `AddPostgresEventStore` and `AddReadModels`, registers `ProjectionStartupCatchUpService` via `AddHostedService<>`, and returns `builder.Build()`. No subcommand parsing; v1 is a single-shape long-running process. Evidence: V7 confirmed zero existing `Host.CreateApplicationBuilder`/`HostApplicationBuilder` references in the codebase, the `Microsoft.Extensions.Hosting.Abstractions` pin in `Directory.Packages.props` covers `IHostedLifecycleService` but not the host-builder API (new meta-package pin needed), and the `EventStore.Postgres.Cli`'s minimal-csproj precedent.

**9. Two environment variables, exit 78 on either missing.** `EVENT_STORE_CONNECTION_STRING` exists from Session 0002 and stays. `READ_MODEL_CONNECTION_STRING` is new. The Session 0005 commit-6 decision to keep two `NpgsqlDataSource` paths independent for the split-database move would be quietly walked back by a one-env-var design. Exit codes mirror the CLI's: 0 success, 1 startup or migration failure, 64 EX_USAGE, 78 EX_CONFIG. `Program.cs` runs `MigrationRunner.RunPendingAsync` on the event-store connection string; if the read-model connection string differs, it runs again on that one. Harmless either way (each database only has its own schemas affected), explicit because the split-database promise pays out here.

**10. End-to-end integration test against `IEventStore.AppendAsync` directly.** The test claim is "a write reaches the read model via LISTEN/NOTIFY," not "the Order aggregate works end to end" (other tests cover that). Direct append keeps the surface narrow and avoids pulling `EventStore.InMemory` into `Workers.Tests` for `EventStoreRepository<TAggregate>`. The test constructs an `EventEnvelope` carrying an `OrderPlaced` payload by hand, calls `eventStore.AppendAsync(streamId, expectedVersion: 0, [envelope], ct)`, polls `order_list` every 50ms with a 2-second upper bound, asserts the row appears and the checkpoint advanced. The 2-second budget is well above the expected sub-100ms LISTEN/NOTIFY path while still failing fast if dispatch breaks. The test does not assert specific latency. Evidence: V7 confirmed `EventStoreRepository<TAggregate>` lives in `EventStore.InMemory` (not `EventStore.Postgres`), so direct-`AppendAsync` is the surface-minimizing path.

## Session 0006 implementation scope

In the natural commit-slicing order:

1. **Handler-level idempotency in `OrderListProjection`.** Add `IOrderListUnitOfWork.GetCheckpointAsync(string projectionName, CancellationToken ct)`. Implementation reads via `ICheckpointStore.GetPositionAsync(name, DbTransaction, ct)` (new overload on `ICheckpointStore` in `Domain.Abstractions`; the existing non-transactional overload stays). `OrderListProjection.HandleAsync` for each of the three handlers reads the checkpoint after `BeginAsync` and returns early if `context.GlobalPosition <= checkpoint`. New `OrderListProjectionTests` methods (one per handler) cover the re-delivery skip; new `PostgresOrderListStoreTests` method covers `GetCheckpointAsync` reads the value advanced within the same transaction; new `PostgresCheckpointStoreTests` method covers the transactional `GetPositionAsync` overload.

2. **`NpgsqlDataSource` disposal-lifetime in `ReadModels.Postgres`.** `NpgsqlReadModelConnectionFactory` gains `IAsyncDisposable` on its declaration and a `DisposeAsync` body that disposes `_dataSource`. New `NpgsqlReadModelConnectionFactoryTests.cs` in `Projections.Tests` with one method asserting `OpenConnectionAsync` throws `ObjectDisposedException` after `DisposeAsync`. New method in `ServiceCollectionExtensions_AddReadModels_Tests` covers the container-to-factory disposal chain.

3. **`MigrationRunner` extracts to `Infrastructure.Migrations.Postgres`.** New project `src/Infrastructure/Migrations.Postgres/Migrations.Postgres.csproj` with one Npgsql package reference, zero project references. Move `MigrationRunner`, `MigrationRunnerOptions`, `MigrationChecksumMismatchException`. Add public `(Assembly assembly, string resourcePrefix)` constructor; remove the implicit parameterless. New `EventStorePostgresMigrations` static class in `EventStore.Postgres` exposes the assembly handle and resource prefix as `public static readonly` members. Update all four call sites (CLI, the two `PostgresFixture` copies, `PostgresMigrationRunnerTests`'s five construction sites) and `EventStore.Postgres.csproj` to add a `ProjectReference` to the new project. Embedded migration `.sql` files stay in `EventStore.Postgres`. The `.slnx` gains the new project. No test method count change.

4. **`EventTypeRegistry` per-bounded-context provider pattern.** New `IEventTypeProvider` in `Domain.Abstractions`. New `SalesEventTypeProvider` in `src/Domain/Sales/`. `EventTypeRegistry` gains non-generic `Register(Type)` and `Register(Type, string)` methods; existing generic overloads delegate to them. `AddPostgresEventStore`'s `EventTypeRegistry` registration becomes a factory that pulls all `IEventTypeProvider` services and populates the registry on first resolution. `TryAddSingleton`, not `AddSingleton`, so host overrides win. New `SalesEventTypeProviderTests` in `Domain.Tests` (one method). New `EventTypeRegistryTests` methods (three: non-generic `Register(Type)`, non-generic `Register(Type, string)`, `!IDomainEvent` rejection). New `ServiceCollectionExtensionsTests` method (`EventStore.Postgres` side) covers provider-driven population.

5. **LISTEN/NOTIFY trigger and listener.** Migration `0005_add_outbox_notify_trigger.sql` creates `event_store.notify_outbox_pending() RETURNS TRIGGER` and the AFTER INSERT FOR EACH STATEMENT trigger on `event_store.outbox`. Add `IdlePollInterval` and `NotificationChannelName` to `OutboxProcessorOptions` (defaults `500ms` and `"outbox_pending"`). Promote `IdlePollInterval` usage in `ExecuteAsync` from the private constant to the options property. Override `StartAsync` on `OutboxProcessor` to open the listener `NpgsqlConnection`, run `LISTEN @channel`, subscribe a `Notification` handler signaling a capacity-1 drop-write `Channel<bool>`, and start a long-running task that loops `NpgsqlConnection.WaitAsync` with reconnect-on-drop. Override `StopAsync` to cancel the listener cts and dispose the connection. The idle wait in `ExecuteAsync` becomes `await Task.WhenAny(notificationTask, Task.Delay(IdlePollInterval, ...))`. New `OutboxNotificationTests.cs` in `Infrastructure.Tests/Postgres/` with two methods (notification-wakes-processor and reconnect-after-disconnect). `PostgresMigrationRunnerTests` count assertions bump for the fifth migration; mechanical, no method count change. Existing `OutboxProcessorTests` stay green (they call `ProcessBatchAsync` directly and never exercise `ExecuteAsync`).

6. **`src/Hosts/Workers/` skeleton plus `ProjectionStartupCatchUpService`.** New `src/Projections/Infrastructure/` subfolder (Session 0005's commit-5 deviation set the trigger as the second projection-side infrastructure type). Move `ProjectionReplayer.cs` into it; namespace shifts to `EventSourcingCqrs.Projections.Infrastructure`. New `ProjectionStartupCatchUpService : IHostedLifecycleService` in the same subfolder, takes `IEnumerable<IProjection>`, `IEventStore`, `ICheckpointStore`, `ILogger<>`, runs sequential catch-up across projections in `StartingAsync`. Empty implementations for `StartAsync`/`StartedAsync`/`StoppingAsync`/`StopAsync`/`StoppedAsync`. New `src/Hosts/Workers/Workers.csproj` with `OutputType=Exe`, one `PackageReference` (`Microsoft.Extensions.Hosting`), four `ProjectReference` entries (event store, read models, migrations, projections; the existing `Domain` reference comes transitively). New `WorkersHostFactory` static class with one `Build(string, string)` method. `Program.cs` is a placeholder for commit 8. New `ProjectionStartupCatchUpServiceTests` in `Projections.Tests` (three methods: replays each projection, uses per-projection checkpoints, propagates cancellation). New `Microsoft.Extensions.Hosting` pin in `Directory.Packages.props`. The `.slnx` gains `src/Hosts/Workers/Workers.csproj`.

7. **`PostgresFixture` extracts to `tests/TestInfrastructure/`.** New `tests/TestInfrastructure/TestInfrastructure.csproj` referencing `Npgsql`, `Testcontainers.PostgreSql`, `Infrastructure.Migrations.Postgres`. Move `PostgresFixture.cs` into it. Update its `MigrationRunner` construction to the new `(Assembly, string)` shape from commit 3. Replace the two existing copies with `ProjectReference` entries from `Infrastructure.Tests` and `Projections.Tests`. The `.slnx` gains `tests/TestInfrastructure/TestInfrastructure.csproj`. No test method count change.

8. **Composition root, migrations orchestration, end-to-end integration test.** `src/Hosts/Workers/Program.cs` reads `EVENT_STORE_CONNECTION_STRING` and `READ_MODEL_CONNECTION_STRING`, exits 78 on either missing. Wires `CancellationTokenSource` to `Console.CancelKeyPress`. Runs `MigrationRunner.RunPendingAsync` on the event-store connection string; if the read-model connection string differs, runs it again. Exits 1 on migration failure. Calls `WorkersHostFactory.Build(eventStoreCs, readModelCs)`, awaits `host.RunAsync(cts.Token)`. New `tests/Workers.Tests/Workers.Tests.csproj` with project references to `Workers`, `EventStore.Postgres`, `ReadModels.Postgres`, `Projections`, `Domain`, `Domain.Abstractions`, `TestInfrastructure`. Two `[Fact]` methods: `WorkersHostFactoryTests.Build_resolves_registered_services` (stub connection strings, assert the hosted services and projection-side registrations) and `WorkersHostIntegrationTests.OrderPlaced_propagates_to_order_list_via_listen_notify` (testcontainer-backed end-to-end). The `.slnx` gains the test project.

**Done when:**

- All 111 existing tests continue to pass after the `IOrderListUnitOfWork.GetCheckpointAsync` addition and the `MigrationRunner` constructor change.
- `dotnet build` and `dotnet test` are green; CI is green on push.
- A locally-run Workers host with `EVENT_STORE_CONNECTION_STRING=...` and `READ_MODEL_CONNECTION_STRING=...` pointed at a fresh PostgreSQL 16 instance applies all five migrations on startup, runs `ProjectionStartupCatchUpService` to no-op completion (no events yet), and idles waiting for outbox notifications. Appending events via a separate `PostgresEventStore` invocation against the same database wakes the dispatcher within sub-100ms wallclock and writes the `order_list` row.
- The end-to-end integration test in `Workers.Tests` passes deterministically (run it ten times locally; no retry, no flakes).

## Project layout after Session 0006

```
src/
  Domain.Abstractions/
    (existing types)
    ICheckpointStore.cs                          // transactional GetPositionAsync overload added
    IEventTypeProvider.cs                        // new
  Domain/
    Sales/
      (existing types)
      SalesEventTypeProvider.cs                  // new
  Hosts/
    Workers/                                     // new project
      Workers.csproj
      Program.cs
      WorkersHostFactory.cs
  Projections/
    Infrastructure/                              // new subfolder
      ProjectionReplayer.cs                      // moved
      ProjectionStartupCatchUpService.cs         // new
    OrderList/
      OrderListProjection.cs                     // idempotency added
      IOrderListStore.cs                         // GetCheckpointAsync added
      OrderListRow.cs                            // unchanged
  Infrastructure/
    Migrations.Postgres/                         // new project
      Migrations.Postgres.csproj
      MigrationRunner.cs                         // moved, (Assembly, string) ctor added
      MigrationRunnerOptions.cs                  // moved
      MigrationChecksumMismatchException.cs      // moved
    EventStore.Postgres/
      EventStorePostgresMigrations.cs            // new
      EventTypeRegistry.cs                       // non-generic Register overloads added
      OutboxProcessor.cs                         // LISTEN/NOTIFY support
      OutboxProcessorOptions.cs                  // IdlePollInterval, NotificationChannelName
      PostgresCheckpointStore.cs                 // transactional GetPositionAsync overload
      ServiceCollectionExtensions.cs             // EventTypeRegistry factory
    ReadModels.Postgres/
      NpgsqlReadModelConnectionFactory.cs        // IAsyncDisposable
      PostgresOrderListUnitOfWork.cs             // GetCheckpointAsync added
migrations/
  0001_initial_event_store.sql                   // existing
  0002_add_outbox_global_position.sql            // existing
  0003_initial_read_models.sql                   // existing
  0004_add_order_list_read_model.sql             // existing
  0005_add_outbox_notify_trigger.sql             // new
tests/
  TestInfrastructure/                            // new project
    TestInfrastructure.csproj
    PostgresFixture.cs                           // moved
  Workers.Tests/                                 // new project
    Workers.Tests.csproj
    WorkersHostFactoryTests.cs
    WorkersHostIntegrationTests.cs
  Projections.Tests/
    OrderListProjectionTests.cs                  // re-delivery skip cases added
    PostgresOrderListStoreTests.cs               // GetCheckpointAsync case added
    PostgresCheckpointStoreTests.cs              // transactional GetPositionAsync case added
    ProjectionStartupCatchUpServiceTests.cs      // new
    NpgsqlReadModelConnectionFactoryTests.cs     // new
    ServiceCollectionExtensions_AddReadModels_Tests.cs  // disposal case added
    PostgresFixture.cs                           // removed (now via ProjectReference)
  Infrastructure.Tests/
    Postgres/
      EventTypeRegistryTests.cs                  // 3 cases added
      OutboxNotificationTests.cs                 // new
      ServiceCollectionExtensionsTests.cs        // provider-population case added
      PostgresMigrationRunnerTests.cs            // 5 ctor sites updated, migration-count assertions bump
      PostgresFixture.cs                         // removed (now via ProjectReference)
  Domain.Tests/
    Sales/
      SalesEventTypeProviderTests.cs             // new
```

## Test count projection

Legend per the Session 0004 convention: **planned methods** = setup-doc projection; actual methods and xUnit cases land in the session log post-execution.

| Checkpoint | Domain.Tests | Infrastructure.Tests | Projections.Tests | Workers.Tests | Total |
| --- | --- | --- | --- | --- | --- |
| Session 0005 baseline | 22 | 63 | 26 | 0 | 111 |
| Commit 1 planned | 22 | 64 | 31 | 0 | 117 |
| Commit 2 planned | 22 | 64 | 33 | 0 | 119 |
| Commit 3 planned | 22 | 64 | 33 | 0 | 119 |
| Commit 4 planned | 23 | 68 | 33 | 0 | 124 |
| Commit 5 planned | 23 | 70 | 33 | 0 | 126 |
| Commit 6 planned | 23 | 70 | 36 | 0 | 129 |
| Commit 7 planned | 23 | 70 | 36 | 0 | 129 |
| Commit 8 planned | 23 | 70 | 36 | 2 | 131 |

Twenty new methods total against the Session 0005 baseline of 111. Commit 4 is the largest test addition (4 new methods across `Domain.Tests` and `Infrastructure.Tests`); commit 3 and commit 7 are pure extractions with no method count change.

## Deferred items and forward references

Carried forward from Session 0005 and earlier, plus new items surfaced in this planning conversation:

1. **Per-event batching for catch-up performance.** Chapter 13's "naive projections suffer most" advice. Not needed at v1 read loads. Flag for a future session if rebuild duration becomes a real concern.
2. **Blue-green and parallel/shadow rebuild patterns.** Chapter 13 documents three rebuild variants; v1 ships in-place rebuild only. Other two land in a later phase if the test suite or AdminConsole earns them.
3. **Per-consumer outbox semantics.** v1's outbox marks `sent_utc` when all handlers agree. Future broker forwarding needs either per-consumer outbox tracking or a separate "broker sent up to position" checkpoint. Flagged for the broker session whenever it arrives.
4. **`customer_name` on `OrderListProjection`.** Awaits Phase 4's customer aggregates and `CustomerSummary` projection.
5. **Application command pipeline and real metadata stamping.** Session 0001's flag 5 stays open. `EventStoreRepository` fills metadata with `Guid.Empty` placeholders and `"Domain"` source. Phase 2's command pipeline replaces the placeholders.
6. **Snapshot infrastructure.** PLAN.md Phase 1 listed `ISnapshotStore` as a Phase 1 deliverable; reality is Phase 12. PLAN.md reconciliation owed in Phase 14 per F-0001-A.
7. **xUnit v3 cancellation-token sweep.** Deferred until Stryker.NET issue #3117 unblocks v3 (ADR 0003).
8. **Read-model split to a separate database.** v1 uses two schemas in one database. Session 0006 keeps the split-database path honest via two environment variables.
9. **Parallel projection catch-up.** Session 0006 ships sequential catch-up across projections in `ProjectionStartupCatchUpService.StartingAsync`. Parallel catch-up earns its complexity when Phase 4's three additional projections show measurable startup cost.
10. **Keyed `NpgsqlDataSource` registrations.** The disposal-lifetime asymmetry between event-store and read-model sides stays deliberate after Session 0006. Keyed-singleton symmetrization deferred unless the asymmetry becomes structurally problematic.
11. **`EventStoreRepository` location.** Currently in `Infrastructure/EventStore.InMemory/` (Session 0001/0002 placement artifact). The class is engine-agnostic and depends only on `IEventStore`. Move to a non-engine-specific project flagged for a later phase.
12. **Per-context module-registration extensions (`AddSalesModule()` etc.).** Session 0006 registers `SalesEventTypeProvider` directly in `WorkersHostFactory.Build`. The per-context extension shape earns its weight when Phase 4 ships Fulfillment plus Billing and the registration-helper pattern composes for real.

## Track A flag candidates this session generates

The implementation closes part of the F-0003-26 cluster scope Session 0005 narrowed. Additional flags this session is likely to generate, pre-recorded so the session log captures them without re-derivation. Final IDs assigned at session-log time.

1. **`ICheckpointStore.GetPositionAsync` transactional overload.** Chapter 13's deep-dive projections either skip the read-inside-transaction question or fold it into prose. The implementation ships two overloads: non-transactional (Session 0005) for catch-up startup reads, transactional (Session 0006) for projection-handler idempotency. Chapter 13 acknowledges the dual-shape and the structural reason (handler-level idempotency closes the at-least-once redelivery window).

2. **`IOrderListUnitOfWork.GetCheckpointAsync` shape.** Chapter 13's prose handles idempotency via either projection-level position-checking or natural-key UPSERT semantics. The implementation chose position-checking via a unit-of-work-side read inside the transaction. Chapter 13 acknowledges the unit-of-work-side shape; the alternative (the projection-level approach) is worth documenting as the alternative path that natural-key UPSERTs make appropriate.

3. **`LISTEN/NOTIFY` trigger as the dispatch wakeup mechanism.** Chapter 8's outbox processor is polling-only with no notification path. The implementation lands an AFTER INSERT FOR EACH STATEMENT trigger calling `pg_notify`, plus a long-lived listener connection on the processor side, with the idle timer as a fallback. Chapter 8 acknowledges the production-pattern delta in one paragraph; the SQL Server adapter in a later phase ships `Service Broker` as the equivalent.

4. **Per-bounded-context `IEventTypeProvider` pattern.** Session 0005's `EventTypeRegistry` resolved logical type names; Session 0006 settles how production code populates it without per-event `Register<T>()` calls in `Program.cs`. Chapter 8's serialization section is the natural reconciliation point.

5. **Static composition factory (`WorkersHostFactory.Build`).** Hosts typically ship as their own `Program.cs` in the chapter narrative. The reference implementation introduces a static factory because the integration test needs the same composition shape `Program.cs` produces. Chapter 18's hosting section could acknowledge the test-driven driver (and the alternative `IHostBuilder` extension shape if the chapter wants a richer treatment).

## Writing rules to apply

From `docs/ai-writing-style-source.txt`:

- No em-dashes in prose. Use commas, parentheses, or full stops.
- No "not just X, it's Y" parallel constructions.
- Drop rule-of-three groupings where two or four items would fit better.
- Cut filler vocabulary: delve, elevate, captivating, genuinely, vivid, comprehensive-as-filler.
- Cut filler intensifiers: specifically, essentially, particularly, actually, honestly, genuinely, basically.
- No empty corporate-polite framing.
- No restating points already made.
- No strained analogies.
- Direct, opinionated, plain prose. Specifics over generic claims.
- First-person where natural.

Voice rules apply to chat prose, code comments, ADRs, commit messages, PR descriptions, and session log content.

## How this session flows

Open Claude Code in the code repo with the standard opening:

> Please re-read CLAUDE.md, docs/PLAN.md, docs/CLAUDE_CODE_PREAMBLE.md, and docs/ai-writing-style-source.txt. Then re-read `docs/sessions/0006-workers-host-setup.md` for the design record this session implements. Then summarize back to me: (1) the work items already complete based on what is in the repo, and (2) the work items still remaining for Session 0006. After I confirm the summary is right, we will start on commit 1.

Session 0006 runs through the eight-commit slice above. Each commit is a logical unit with its own tests passing before the next commit starts. Propose-before-writing applies per CLAUDE_CODE_PREAMBLE.md. Stop and surface if the agreed design turns out to be incorrect during execution.

Track A flags discovered during execution land in the session log under "Cross-track flags." The session log lives at `docs/sessions/0006-workers-host.md` post-execution; this setup document archives at `docs/sessions/0006-workers-host-setup.md`.
