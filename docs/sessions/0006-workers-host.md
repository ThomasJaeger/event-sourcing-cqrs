# Session 0006: Workers host

Date: 2026-05-16
Phase: between Phase 6 and Phase 7 per PLAN.md; closing piece of the runnable-Order-workflow window in the reconciled six-week foundation plan. PLAN.md reconciliation owed in Phase 14 per F-0001-A.

## Status

Shipped 2026-05-16. Eight commits implementing the design record in `docs/sessions/0006-workers-host-setup.md`.

1. `0b89576` — handler-level idempotency in OrderListProjection (+5 xUnit cases)
2. `4247363` — NpgsqlReadModelConnectionFactory IAsyncDisposable (+2)
3. `b01c739` — extract MigrationRunner to Infrastructure.Migrations.Postgres (0, pure refactor)
4. `701e81a` — IEventTypeProvider per-bounded-context (+5)
5. `ba1fd07` — LISTEN/NOTIFY trigger and listener in OutboxProcessor (+2)
6. `db46f9b` — Workers host skeleton + ProjectionStartupCatchUpService (+3)
7. `96c91b8` — extract PostgresFixture to tests/TestInfrastructure (0, pure extraction)
8. `d84a300` — composition root + end-to-end LISTEN/NOTIFY integration test (+2)

End state: 130 xUnit cases (Domain.Tests 23, Infrastructure.Tests 69, Projections.Tests 36, Workers.Tests 2).

## Scope

The Workers host: the first long-running process that composes every adapter end-to-end. Specifically, handler-level redelivery idempotency on the projection's write path; `IAsyncDisposable` plumbing on the read-model connection factory; extraction of `MigrationRunner` from `EventStore.Postgres` into its own engine-specific project; per-bounded-context event-type registration via `IEventTypeProvider`; LISTEN/NOTIFY trigger plus listener for sub-second outbox dispatch; the Workers host skeleton with a startup catch-up service running in `IHostedLifecycleService.StartingAsync`; extraction of `PostgresFixture` into a shared `tests/TestInfrastructure` project; and the composition root in `Program.cs` with two-env-var configuration, migration orchestration, and the end-to-end integration test.

Out of scope: the four other event-store adapters; the other three projections; the UI and API hosts; per-event catch-up batching; parallel projection catch-up. The full deferred list lives in the setup document.

## Methodology: seven-round read-only verification protocol

This session introduced a verification protocol for the Track B planning conversation: before each commit's design was finalized, the planner issued a read-only one-shot prompt to the code-repo Claude Code to verify structural assumptions against actual source. Seven rounds, one per commit's surface area. The protocol cost a few minutes per commit; it caught several structural mistakes that would otherwise have surfaced as mid-execution re-plans.

**What the protocol caught.** V3 confirmed the parameterless `MigrationRunner` ctor at all four call sites and the absence of internal coupling beyond `MigrationFile`, which scoped commit 3's extraction cleanly. V4 confirmed the `EventTypeRegistry`'s host-side registration had no constructor — and pivoted commit 4's design from a push-shape (registry consumers calling `Register<T>()` during composition) to a pull-shape (`IEventTypeProvider` declares types, the registry consumer walks providers). The pivot kept `Domain.Abstractions` independent of the registry's location. V7 confirmed zero existing `IHostedLifecycleService` references and the `ProjectionReplayer(IEventStore, IProjection)` constructor shape, which grounded commit 6's design without back-and-forth on the lifecycle-pipeline ordering. Several smaller shape confirmations: the outbox-write SQL and the absence of any pg_notify trigger or `OutboxProcessor.ExecuteAsync` test (V5); the unit-of-work's `_transaction` and `_checkpointStore` field shape (V1, used in the idempotency design); the embedded-resource declaration in `EventStore.Postgres.csproj` (V3).

**What the protocol missed, and how.** V1 verified the field declarations of `PostgresOrderListUnitOfWork` and the `IAsyncDisposable` contract, but did not verify the SQL body of `InsertAsync`. The setup doc's justification ("Without this the OrderPlaced handler's INSERT on (order_id) PK conflicts on any re-delivery") was therefore wrong on its facts: `InsertAsync` already uses `ON CONFLICT (order_id) DO NOTHING`, so insert redelivery was already harmless at the SQL level. The real justification is UPDATE-handler reordering: a stale `OrderShipped` arriving after `OrderCancelled` would clobber Cancelled back to Shipped, and the position check is what catches that. It is harder to argue away than the setup doc's framing because the SQL-level fix only covers one of the three handlers (the insert), while the position check covers all three. V1 also did not verify which test project housed `PostgresCheckpointStoreTests`. The setup-doc test-count table placed commit 1's `+1` for that file in `Infrastructure.Tests`; the file actually lives in `Projections.Tests` (Session 0005's project layout). The off-by-one carried through every subsequent commit's projected total.

**Cost-benefit.** Net positive. The protocol's two misses were both at V1, and both were facts that one additional read each would have surfaced. The two refinements (the idempotency justification and the column-accounting deviation) cost less than the structural re-plans V3/V4/V7 would have triggered without verification. For future sessions, V1-style verification should explicitly include the body of methods whose contract decisions depend on (not just field shapes) and the file-system location of every test class the count projection touches.

## Per-commit notes

### Commit 1, `0b89576`: handler-level idempotency in OrderListProjection

What shipped: `ICheckpointStore` gains a transactional `GetPositionAsync(name, DbTransaction, ct)` overload alongside the existing non-transactional read used by the catch-up service; `PostgresCheckpointStore` implements it via the caller's transaction's connection. `IOrderListUnitOfWork` gains `GetCheckpointAsync(projectionName, ct)`, implemented by delegating to the transactional overload on the unit-of-work's open transaction. `OrderListProjection`'s three handlers each open the unit of work, read the checkpoint, and return early when `context.GlobalPosition <= checkpoint`. The early-return path disposes the unit of work without commit, rolling the empty transaction back, so the skip costs one `SELECT`.

Tests: +5 in `Projections.Tests`. Three new `OrderListProjectionTests` methods (one per handler) exercise the redelivery-skip path via `InsertCount` and `UpdateCount` counters on `InMemoryOrderListStore`, since simply asserting the row's final state would also be true under the SQL-level idempotency the projection already had. One new `PostgresOrderListStoreTests` method exercises `GetCheckpointAsync` reading a previously-committed advance. One new `PostgresCheckpointStoreTests` method exercises read-your-own-writes through the transactional overload.

Deviation from the setup doc's justification: the setup doc framed handler-level idempotency as defense against insert-redelivery PK conflicts. `PostgresOrderListUnitOfWork.InsertAsync` already uses `ON CONFLICT (order_id) DO NOTHING`, so that path was already harmless at the SQL level. The real justification — surfaced at commit-1 verification time — is UPDATE-handler reordering: the position check covers all three handlers, whereas the SQL-level fix only protected the insert.

Test-count deviation: +5 actual vs +6 projected. The setup-doc table placed `PostgresCheckpointStoreTests`'s new method in the Infrastructure.Tests column; the file lives in `Projections.Tests` (Session 0005 placement), so the `+1` lands in `Projections.Tests` (already counted in the +5 above). The Infrastructure column projection from this commit onward is consistently `+1` too high. Carries through every subsequent commit's projected total; final session total 130 actual vs 131 projected.

### Commit 2, `4247363`: NpgsqlReadModelConnectionFactory IAsyncDisposable

What shipped: `NpgsqlReadModelConnectionFactory` declares `IAsyncDisposable`; `DisposeAsync` forwards to the underlying `NpgsqlDataSource`. The `IReadModelConnectionFactory` interface stays bare; disposal is an Npgsql-implementation concern, not a contract concern. A one-paragraph comment on the factory class explains why the container disposes the factory based on the concrete instance's interface (runtime-type check), so `AddReadModels` needs no registration change.

Tests: +2 in `Projections.Tests`. New `NpgsqlReadModelConnectionFactoryTests` with one method asserting `OpenConnectionAsync` throws `ObjectDisposedException` after `DisposeAsync` (factory level). New method on `ServiceCollectionExtensions_AddReadModels_Tests` asserting the container-to-factory disposal chain via `await provider.DisposeAsync()`.

In-scope adjustment: the existing `AddReadModels_resolves_the_read_side_service_graph` test converted from sync `void`/`using var provider` to `async Task`/`await using var provider`. Microsoft DI's `ServiceProvider.Dispose` throws `InvalidOperationException` on `IAsyncDisposable`-only singletons by design ("Use DisposeAsync to dispose the container"). The Workers host's `host.RunAsync` path uses async disposal, so production wiring is unaffected; future tests building a provider against the read-side graph must use the async-disposal pattern.

### Commit 3, `b01c739`: extract MigrationRunner to Infrastructure.Migrations.Postgres

What shipped: new `src/Infrastructure/Migrations.Postgres` project with one Npgsql package reference and zero project references. `MigrationRunner`, `MigrationRunnerOptions`, and `MigrationChecksumMismatchException` moved into it; namespace shifts to `EventSourcingCqrs.Infrastructure.Migrations.Postgres`. `MigrationRunner` gains a public `(Assembly, string resourcePrefix)` ctor in place of the parameterless one; `LoadEmbeddedMigrations` becomes a non-static instance method reading from the ctor-supplied fields. A new `EventStorePostgresMigrations` static class in `EventStore.Postgres` owns the assembly handle and the resource prefix in one place. `EventStore.Postgres.csproj` gains a `ProjectReference` to the new project; the embedded `.sql` resources stay in `EventStore.Postgres`. Eight call sites updated across four files (CLI Program.cs, both `PostgresFixture` copies, five sites in `PostgresMigrationRunnerTests`).

Tests: unchanged. Pure refactor.

Deviation: setup-doc decision 4 specified `public static readonly Assembly` and `public static readonly string` for `EventStorePostgresMigrations`. The shipped form keeps the `Assembly` field as `static readonly` but the `ResourcePrefix` is `public const string`. The prefix string is genuinely a compile-time constant tied to the csproj's `LogicalName` attribute, intra-repo consumers all recompile together, and `const` is idiomatic for this case. The deviation is cosmetic; the setup doc was imprecise.

### Commit 4, `701e81a`: IEventTypeProvider per-bounded-context

What shipped: `IEventTypeProvider` in `Domain.Abstractions` with one method `IEnumerable<Type> GetEventTypes()`. `SalesEventTypeProvider` in `src/Domain/Sales/` returns the seven Order events in canonical lifecycle order. `EventTypeRegistry` gains non-generic `Register(Type)` and `Register(Type, string)` overloads with a runtime `IDomainEvent` check; the existing generic overloads delegate to them so all eight existing call sites stay unchanged. `AddPostgresEventStore`'s registry registration becomes a `TryAddSingleton<EventTypeRegistry>` factory that walks every registered `IEventTypeProvider` on first resolution.

Tests: +5. One in `Domain.Tests` (`SalesEventTypeProviderTests` asserting the seven events in canonical order). Three in `Infrastructure.Tests` (`EventTypeRegistryTests` exercising the two non-generic overloads plus the runtime `IDomainEvent` rejection with a private `NotAnEvent` class). One in `Infrastructure.Tests` (`ServiceCollectionExtensionsTests` provider-population). The provider-population test registers the providers *after* `AddPostgresEventStore` in the service collection and asserts the registry still populates — exercising the structural property that `GetServices<IEventTypeProvider>()` enumerates at first resolution, not at registration time.

### Commit 5, `ba1fd07`: LISTEN/NOTIFY trigger and listener in OutboxProcessor

What shipped: migration 0005 with the `event_store.notify_outbox_pending()` plpgsql function and an `AFTER INSERT FOR EACH STATEMENT` trigger on `event_store.outbox` calling `pg_notify('outbox_pending', '')`. Per-statement (not per-row) coalesces batched appends into one notification per commit; `pg_notify` fires at COMMIT, so listeners see notifications only after the outbox row is visible. `OutboxProcessorOptions` gains `IdlePollInterval` (`TimeSpan`, default 500ms, promoted from the previous static constant) and `NotificationChannelName` (`string`, default `"outbox_pending"`). `OutboxProcessor` gains `StartAsync`/`StopAsync` overrides on `BackgroundService`: a long-lived listener `NpgsqlConnection` acquired through the existing `INpgsqlConnectionFactory` contract, kept open across the processor's lifetime, runs `LISTEN` against the configured channel. A long-running `ListenAsync` loop awaits `NpgsqlConnection.WaitAsync`; on connection drop it disposes, sleeps 1s, reopens, re-LISTENs, continues. `ExecuteAsync`'s idle wait snapshots the current `TaskCompletionSource<bool>` before each batch and awaits either it or an `IdlePollInterval` timer fallback; the swap-after-wait moves to a fresh TCS for the next iteration. `Volatile.Read`/`Volatile.Write` serialize the publish ordering between the listener handler and the processor loop. Channel name is identifier-quoted with doubled-quote escaping at the `LISTEN` site.

Tests: +2 in `Infrastructure.Tests/Postgres/OutboxNotificationTests.cs`. The notification-wakes test sets `IdlePollInterval` to 30 seconds and asserts the processor wakes within 2 seconds of an outbox INSERT — the 30s/2s gap makes the wake unambiguously notification-driven. The reconnect-after-drop test terminates the listener's backend session via `pg_terminate_backend` (filter accepts both bare `LISTEN%` and any wrapper to survive future Npgsql keepalive behavior), waits 2.5s for the reconnect cycle, seeds a fresh row, asserts wake within the same 2s window. Mechanical: `PostgresMigrationRunnerTests` migration-count assertions bump from 4 to 5 across three methods.

### Commit 6, `db46f9b`: Workers host skeleton + ProjectionStartupCatchUpService

What shipped: `ProjectionReplayer.cs` moves from `src/Projections/` to `src/Projections/Infrastructure/`; namespace shifts to `EventSourcingCqrs.Projections.Infrastructure`. New `ProjectionStartupCatchUpService : IHostedLifecycleService` in the same subfolder; `StartingAsync` runs sequential catch-up across every registered `IProjection` by reading each projection's checkpoint via the non-transactional `ICheckpointStore.GetPositionAsync` overload and constructing a `ProjectionReplayer` per projection. The other five lifecycle methods are no-ops. New `src/Hosts/Workers/Workers.csproj` (`OutputType=Exe`, one `Microsoft.Extensions.Hosting` PackageReference, four ProjectReferences). New `WorkersHostFactory.Build(string, string)` returns `IHost`; registers `SalesEventTypeProvider`, calls `AddPostgresEventStore` and `AddReadModels`, registers `ProjectionStartupCatchUpService` as a hosted service. Placeholder `Program.cs` for commit 8 to replace. `Microsoft.Extensions.Hosting` 10.0.0 added to `Directory.Packages.props`. The `.slnx` gains the Workers project.

Tests: +3 in `Projections.Tests/ProjectionStartupCatchUpServiceTests.cs`. Replays each projection from its checkpoint; per-projection checkpoints drive independent replays; pre-cancelled token propagates as `OperationCanceledException`. Uses in-file private `FakeEventStore`, `RecordingProjection`, and `InMemoryCheckpointStore` fakes — parallel to those in `ProjectionReplayerTests` rather than shared, because the two test files examine different invariants and a shared fake would either grow to cover both or get awkwardly subclassed.

In-scope adjustment: `Projections.csproj` gained `Microsoft.Extensions.Hosting.Abstractions` (for `IHostedLifecycleService`) and `Microsoft.Extensions.Logging.Abstractions` (for `ILogger<>`) PackageReferences. Both versions already pinned centrally; surfaced during build because the new service uses types from both directly. The two `.gitkeep` placeholders in `src/Hosts/Workers/` and `src/Projections/Infrastructure/` are removed; the directories now contain real files.

### Commit 7, `96c91b8`: extract PostgresFixture to tests/TestInfrastructure

What shipped: new `tests/TestInfrastructure/TestInfrastructure.csproj` (minimal library, not a test runner). Three PackageReferences (Npgsql, Testcontainers.PostgreSql, xunit for `IAsyncLifetime`), two ProjectReferences (`Migrations.Postgres` for the runner, `EventStore.Postgres` for the `EventStorePostgresMigrations` assembly handle and resource prefix). `PostgresFixture.cs` moves to `tests/TestInfrastructure/`, namespace `EventSourcingCqrs.TestInfrastructure`. Both old copies deleted. The new canonical file's doc comment references the three consuming assemblies including the upcoming `Workers.Tests` and drops the Session 0005 deferral comment that the `Projections.Tests` copy carried (now paid off). Two consumer csprojs gain a `ProjectReference` to `TestInfrastructure`; direct Npgsql and Testcontainers.PostgreSql PackageReferences stay in both (pruning would be out-of-scope for an extraction commit). 9 consumer test files gain a `using EventSourcingCqrs.TestInfrastructure;` directive (6 in `Infrastructure.Tests`, 3 in `Projections.Tests`). `.slnx` updated.

Tests: unchanged. Pure extraction.

In-scope adjustment: `IsTestProject=false` opt-out in `TestInfrastructure.csproj`. Without it, the `xunit` package reference triggered SDK test-host discovery on the library assembly, which aborted on a missing `Docker.DotNet` runtime dependency. Surfaced during local test run; the opt-out plus an inline comment in the csproj closes the surprise for CI and fresh-machine setups.

Deviation: git's rename-detection heuristic picked the `Projections.Tests` copy as the rename source rather than the `Infrastructure.Tests` copy that the proposal planned to seed from. The new file's content was seeded from the Infrastructure.Tests version (older Session 0002 lineage, no deferral comment), but the new doc comment differs from both old comments and git's content-similarity scoring picked the Projections.Tests copy as the closer match. The commit message was adjusted at commit time so the narrative matches `git log --follow` lineage rather than the seeding intent. Practical outcome identical: both old copies are gone, the new canonical file stands.

### Commit 8, `d84a300`: composition root + end-to-end LISTEN/NOTIFY integration test

What shipped: `src/Hosts/Workers/Program.cs` replaces the placeholder. Reads `EVENT_STORE_CONNECTION_STRING` and `READ_MODEL_CONNECTION_STRING` (exit 78 `EX_CONFIG` if either missing). Wires `CancellationTokenSource` to `Console.CancelKeyPress`. Runs `MigrationRunner.RunPendingAsync` on the event-store connection string first (so migration 0005's pg_notify trigger lands before the host's listener starts), then again on the read-model connection string only when it differs by ordinal compare. Exits 1 on migration failure. Builds the host via `WorkersHostFactory.Build`, calls `host.RunAsync(cts.Token)`. New `tests/Workers.Tests/Workers.Tests.csproj` with 7 ProjectReferences. `WorkersHostFactoryTests.Build_resolves_registered_services` builds the host against stub connection strings (no resolution path opens a connection) and asserts the full service graph. `WorkersHostIntegrationTests.OrderPlaced_propagates_to_order_list_via_listen_notify` is testcontainer-backed: starts the host, calls `IEventStore.AppendAsync` with an OrderPlaced envelope, polls `IOrderListStore.GetAsync` every 50ms for up to 2 seconds, asserts the row arrives.

Tests: +2 in `Workers.Tests`. Build fix: `using Microsoft.Extensions.Hosting;` needed in Program.cs for the `RunAsync` extension method on `IHost`; surfaced during build.

Verification: 10 successive runs of the integration test, all passed, durations 383-441ms. Well under the 500ms `IdlePollInterval` fallback in every run, confirming the LISTEN/NOTIFY path (not the timer) is what wakes the processor end-to-end.

Finding (not a deviation): `using var host = WorkersHostFactory.Build(...)` is safe over the `IAsyncDisposable`-only `NpgsqlReadModelConnectionFactory` singleton, even though the equivalent `using var provider = services.BuildServiceProvider()` pattern threw in commit 2. `Host.Dispose` is implemented as sync-over-async to `Host.DisposeAsync`, which routes through `ServiceProvider.DisposeAsync` (the async path that handles `IAsyncDisposable`-only singletons correctly). The direct `ServiceProvider.Dispose` path that commit 2 hit is bypassed entirely. Inline comment in Program.cs records the reason.

## Internal notes

**`CLAUDE_CODE_PREAMBLE.md` lives at the repo root, not under `docs/`.** The session's opening summary noted it as absent because the initial scan looked under `docs/`. It is present, alongside `CLAUDE.md` and `Directory.Build.props`. The working pattern documented there (propose-before-writing, stop-and-surface, log cross-track flags, commit-per-logical-unit, build-green-between-steps) matches what the session followed; no course correction was needed. Worth recording so a future session's opening scan checks both locations.

**`Projections.csproj` PackageReference adds were transitive-reference gaps surfaced by direct type use.** The two adds in commit 6 (`Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`) are not new dependencies in any architectural sense — both packages were already pinned in `Directory.Packages.props` from earlier sessions, and both were transitively reachable through other ProjectReferences. The csproj edits closed gaps that surfaced when the new `ProjectionStartupCatchUpService` named the types directly. Pattern worth noting: SDK transitive references reach the compiler when an upstream project uses the types, but consuming projects that name the types directly need their own PackageReference for the compile to resolve.

**`BuildEnvelope` is duplicated in `Workers.Tests`, not promoted from `PostgresEventStoreTestKit`.** The test kit's helper is `internal static` to `Infrastructure.Tests`; promoting it would either move it to `TestInfrastructure` (extending that project's scope from fixture-only to test-helper-bag) or add `InternalsVisibleTo`. Neither earns its weight at two callers. Forward flag: a third caller of the same envelope-building shape triggers the extraction. Same reasoning as the Session 0005 reflection-extraction trigger and the Session 0004 parameter-binding-helper duplication.

**`OutboxProcessor.ListenAsync`'s reconnect path holds a 1-second delay constant, not an option.** Promoting `ListenerReconnectDelay` to `OutboxProcessorOptions` was considered and deferred. The only test that observes the delay (`Reconnect_after_listener_connection_drop_restores_wakeups`) waits 2.5s for the reconnect cycle, comfortably accommodating the 1s constant. Production tuning needs would earn the option promotion; v1 does not have that pressure.

## Test count

Legend per the Session 0004 convention: **planned methods** = setup-doc projection; **methods** = test methods Track C wrote; **xUnit cases** = test-runner total including `[Theory]` row expansion.

| Checkpoint | Domain.Tests | Infrastructure.Tests | Projections.Tests | Workers.Tests | Total |
| --- | --- | --- | --- | --- | --- |
| Session 0005 actual xUnit cases | 22 | 63 | 26 | 0 | 111 |
| Commit 1 actual xUnit cases | 22 | 63 | 31 | 0 | 116 |
| Commit 2 actual xUnit cases | 22 | 63 | 33 | 0 | 118 |
| Commit 3 actual xUnit cases | 22 | 63 | 33 | 0 | 118 |
| Commit 4 actual xUnit cases | 23 | 67 | 33 | 0 | 123 |
| Commit 5 actual xUnit cases | 23 | 69 | 33 | 0 | 125 |
| Commit 6 actual xUnit cases | 23 | 69 | 36 | 0 | 128 |
| Commit 7 actual xUnit cases | 23 | 69 | 36 | 0 | 128 |
| Commit 8 actual xUnit cases | 23 | 69 | 36 | 2 | 130 |

Planned versus actual. The setup document projected 131 total at commit 8 (Domain 23, Infrastructure 70, Projections 36, Workers.Tests 2). Actual is 130, with Infrastructure 69 instead of 70. The single-case difference traces to commit 1's column-accounting deviation: the setup-doc table placed `PostgresCheckpointStoreTests`'s new method in the Infrastructure.Tests column, but that test class lives in `Projections.Tests` (Session 0005 placement). The Infrastructure column projection from commit 1 onward is consistently 1 too high; the Projections column at commit 1 already counts the case correctly (`+5` in the actual row vs `+5` in the projected row), and the total carries the off-by-one. Surfaced at commit-1 verification time and tracked across every subsequent commit; the cost of fixing the projection retroactively was higher than tracking it forward, so the setup doc stays as committed and this deviation note carries the reconciliation.

Per-commit method-level counts. Commit 1: 3 new `OrderListProjectionTests` methods (redelivery-skip per handler), 1 new `PostgresOrderListStoreTests` method (`GetCheckpointAsync` inside a unit of work), 1 new `PostgresCheckpointStoreTests` method (transactional `GetPositionAsync` with read-your-own-writes). Commit 2: 1 new `NpgsqlReadModelConnectionFactoryTests` method, 1 new method on the existing `ServiceCollectionExtensions_AddReadModels_Tests`. Commit 3: zero new methods, pure refactor. Commit 4: 1 in Domain.Tests (`SalesEventTypeProviderTests`), 3 new `EventTypeRegistryTests` methods (two non-generic overloads plus `!IDomainEvent` rejection), 1 new method on the existing event-store `ServiceCollectionExtensionsTests`. Commit 5: 2 new `OutboxNotificationTests` methods. Commit 6: 3 new `ProjectionStartupCatchUpServiceTests` methods. Commit 7: zero new methods, pure extraction. Commit 8: 2 new methods across `WorkersHostFactoryTests` and `WorkersHostIntegrationTests`.

Container startup and warm-cache wall time. `Infrastructure.Tests` ran 3s warm at baseline, ~7s after commits 5 and 8 (the LISTEN/NOTIFY tests and the integration test both run host lifecycle code in the body; the 2.5s reconnect wait in `Reconnect_after_listener_connection_drop_restores_wakeups` is the bulk of the delta). `Projections.Tests` stayed at ~4s. `Workers.Tests` cold runs ~4s end-to-end, dominated by container startup; the integration test itself runs in 383-441ms once warm (10/10 runs verified).

## Cross-track flags (Track A)

Final IDs are assigned when the consolidated `cross-track-flags-summary.md` index in the book repo is updated post-session. Two groups: flags discovered during execution, and the setup document's pre-recorded candidates with how the implementation resolved each.

### Discovered during execution

1. **SQL-level and projection-level idempotency are layers, not alternatives.** Chapter 13's idempotency discussion presents projection-level position-checking and natural-key UPSERT semantics as two alternative approaches to dealing with at-least-once redelivery. The reference implementation ships both: `ON CONFLICT (order_id) DO NOTHING` on `PostgresOrderListUnitOfWork.InsertAsync` (Session 0005's commit-4 work), plus handler-level position-checking on the projection side (this session's commit 1). Together they form defense-in-depth: the SQL-level approach catches insert redelivery at the storage boundary; the projection-level approach catches UPDATE-handler reordering structurally (a stale `OrderShipped` arriving after `OrderCancelled` would clobber state without the position check, regardless of the insert's `ON CONFLICT` clause). Chapter 13 reconciliation: present them as layers with distinct strengths, not as either/or.

2. **Split-database migration orchestration in the host's Program.cs.** Chapter 18's hosting section does not currently address what a host should do when event-store and read-model connection strings differ. The reference `Program.cs` runs migrations against the event-store connection string first (so migration 0005's pg_notify trigger lands before the listener starts), then against the read-model connection string only when it differs by ordinal compare. The pattern generalizes to any split-deployment scenario where two adapter assemblies own different schemas in the same database engine. The ordering and the `Ordinal` compare both matter: ordering because the listener depends on the trigger; ordinal compare so a whitespace difference does not skip the second run when the operator intends a separate database.

### Pre-recorded candidates from the setup document, resolved

The setup document pre-recorded five flag candidates this session was expected to generate. Each resolved as follows:

3. **`ICheckpointStore.GetPositionAsync` transactional overload.** Chapter 13's deep-dive projections either skip the read-inside-transaction question or fold it into prose. The implementation ships two overloads: non-transactional (Session 0005) for catch-up startup reads, transactional (Session 0006 commit 1) for projection-handler idempotency. Chapter 13 acknowledges the dual shape and the structural reason (handler-level idempotency closes the at-least-once redelivery window without depending on storage-side guarantees).

4. **`IOrderListUnitOfWork.GetCheckpointAsync` shape.** Chapter 13's prose handles idempotency via either projection-level position-checking or natural-key UPSERT semantics. The implementation chose position-checking via a unit-of-work-side read inside the transaction. Chapter 13 acknowledges the unit-of-work-side shape and the alternative (projection-level approach) that natural-key UPSERTs make appropriate.

5. **`LISTEN/NOTIFY` trigger as the dispatch wakeup mechanism.** Chapter 8's outbox processor is polling-only with no notification path. The implementation lands an `AFTER INSERT FOR EACH STATEMENT` trigger calling `pg_notify`, plus a long-lived listener connection on the processor side, with the idle timer as fallback. Chapter 8 acknowledges the production-pattern delta in one paragraph; the SQL Server adapter in a later phase ships `Service Broker` as the equivalent.

6. **Per-bounded-context `IEventTypeProvider` pattern.** Session 0005's `EventTypeRegistry` resolved logical type names; this session's commit 4 settles how production code populates it without per-event `Register<T>()` calls in `Program.cs`. Chapter 8's serialization section is the natural reconciliation point. The pull-shape choice (provider declares types, registry consumer walks) keeps Domain.Abstractions independent of the registry's location.

7. **Static composition factory (`WorkersHostFactory.Build`).** Hosts typically ship as their own `Program.cs` in the chapter narrative. The reference implementation introduces a static factory because the integration test needs the same composition shape `Program.cs` produces. Chapter 18's hosting section acknowledges the test-driven driver pattern, with the alternative `IHostBuilder` extension shape as the richer treatment.

## Notes for next sessions

**CLAUDE_CODE_PREAMBLE.md / docs/PLAN.md reconciliation owed by F-0001-A.** The path-correction finding (preamble lives at repo root, not under `docs/`) plus the persistent Phase-6-versus-reconciled-plan numbering both feed this. The Session 0005 log already records the PLAN.md reconciliation as owed in Phase 14; the preamble-path note adds a small line item to that pass.

**Phase 4 work per PLAN.md.** Three additional aggregates: Inventory and Shipment (Fulfillment context), Payment (Billing context). Each new context's `IEventTypeProvider` mirrors `SalesEventTypeProvider`. The projection-registration-helper question Session 0005 deferred (`AddReadModels` hand-writes five registrations for one projection) gets a real second-projection answer when `CustomerSummaryProjection` ships in Phase 4 alongside the customer aggregates.

**LISTEN/NOTIFY determinism baseline (10/10 passes, 383-441ms).** Future regressions against the dispatch path should fail loudly against this envelope. A test that takes more than ~500ms is no longer notification-wake bound; it is hitting the idle-poll fallback, which signals a regression in the listener loop, the trigger function, the channel routing, or the swap-after-wake race.

**Reflection-extraction trigger still pending.** `InProcessMessageDispatcher` and `ProjectionReplayer` each carry their own closed-generic reflection (resolve `IEventHandler<TEvent>.HandleAsync` and `EventContext<TEvent>` constructor). They are not shared because their drivers differ. A third consumer of that reflection earns the extraction; none arrived in this session.

**`BuildEnvelope` test helper has two callers now.** The original in `PostgresEventStoreTestKit` (internal-static to `Infrastructure.Tests`) and the duplicate in `Workers.Tests/WorkersHostIntegrationTests.cs`. Same trigger as the reflection extraction: a third caller earns promotion to `TestInfrastructure` or an `InternalsVisibleTo` arrangement.
