# Session Setup: Session 0005 — Weeks 5-6 first projection

This document captures the state of the build at the close of Session 0004 and the design decisions settled in the Session 0005 planning conversation. It bridges the planner conversation to the next code-repo Claude Code execution session.

Save this version as `docs/sessions/0005-weeks5-6-first-projection-setup.md` after the next session begins. The post-execution session log lands at `docs/sessions/0005-weeks5-6-first-projection.md` per the two-file convention from Session 0002 (`-setup.md` for planning input, no suffix for session log).

## The reference implementation, current state

Public repo at github.com/ThomasJaeger/event-sourcing-cqrs. MIT licensed. .NET 10 LTS, C# 14, xUnit v2, FluentAssertions v7, Testcontainers for PostgreSQL integration. CI green on every push.

Phase 1 (Weeks 1-2 in-memory foundation) and Phase 2's three PostgreSQL sessions (Weeks 3-4, Sessions 0002 through 0004) are complete. Session 0005 is the first projection and the read side, Weeks 5-6 of the reconciled six-week foundation plan. PLAN.md numbers this work as Phase 6 (Weeks 11-12); the reconciled plan pulls it forward so the Order workflow becomes runnable end-to-end before the other aggregates ship. PLAN.md reconciliation is owed in Phase 14 per F-0001-A.

## Where the build stands at the end of Session 0004

After Session 0004:

- `EventStore.Postgres` ships the full Phase 2 PostgreSQL slice: schema migrations via `MigrationRunner`, `PostgresEventStore` implementing `IEventStore` with atomic events-plus-outbox writes, `OutboxProcessor` draining the outbox by polling with backoff and quarantine, `InProcessMessageDispatcher` resolving `IEventHandler<TEvent>` from DI, and the `AddPostgresEventStore` composition-root extension.
- `Domain.Abstractions` contains the core ports: `AggregateRoot`, `IDomainEvent`, `IEventStore`, `IEventStoreRepository<TAggregate>`, `EventEnvelope`, `EventMetadata`, `ConcurrencyException`, `DomainException`, `IMessageDispatcher`, `OutboxMessage`, `IEventHandler<TEvent>`.
- The Order aggregate is built and tested with full lifecycle coverage per Session 0001.
- 52 tests across `Domain.Tests` (22) and `Infrastructure.Tests` (30). All green. Warm-cache duration ~3.0s for Infrastructure.Tests.

What is not yet in place:

- No Workers host. `AddPostgresEventStore` is wired but not consumed by any `Program.cs`.
- No projections. The handler side of `InProcessMessageDispatcher` resolves into an empty set.
- No checkpoint store, no read-model store, no replayer infrastructure.
- No read-model schema. The `event_store` schema is the only schema in the database.

## Decisions locked across Sessions 0001-0004

From Session 0001:

- `IEventStoreRepository<TAggregate>`, not `IRepository<T>`. Chapter 8's repository shape.
- `AggregateRoot` as abstract base class, not interface.
- `IEventStore.ReadStreamAsync` has `fromVersion = 0` default.
- `EventEnvelope` is the C# boundary type, payload typed as `IDomainEvent`.
- In-memory event store is teaching scaffolding plus an `Application.Tests` fixture, not a fourth peer.
- `Money.Zero` carries currency-less identity.
- `DomainException` for business-rule violations.
- Test naming: snake_case sentence form.

From Session 0002:

- Event-store schema with `global_position BIGINT GENERATED ALWAYS AS IDENTITY` as PK on `event_store.events`, plus `(stream_id, stream_version)` UNIQUE for optimistic concurrency, `event_id` UNIQUE for idempotency.
- STORED generated columns for `correlation_id` and `causation_id`.
- Outbox shape: `event_store.outbox` and `event_store.outbox_quarantine`, partial index `ix_outbox_pending` ordering pending rows FIFO.
- Hand-rolled migrations in `/migrations/*.sql`, applied by `MigrationRunner` in `EventStore.Postgres`, `pg_advisory_lock`-guarded, SHA-256 checksum verified.
- CLI via `EventStore.Postgres.Cli` with exit codes 0/1/64/78.
- Code-first vs manuscript routing rule: implementation choice wins, Track A flag logged. Three carve-out conditions trigger itemized impact discussion; otherwise the flag in the session log is the full record.

From Session 0003:

- `ConcurrencyException` collapsed to two-argument shape (`StreamId`, `ExpectedVersion`).
- `EventTypeRegistry` resolves logical event-type names to CLR types. Lives in `Infrastructure/EventStore.Postgres` for now; moves to `Infrastructure/Versioning` in Phase 12.
- Registry is authoritative for storage-side type names; envelope's `EventType` field is informational.
- Filtered `23505` mapping: only `uq_events_stream_version` violations become `ConcurrencyException`.
- Snake-case JSON keys (`JsonNamingPolicy.SnakeCaseLower`) validated end to end.
- ADR 0004: self-contained event store adapters. Each adapter owns its row construction, INSERT SQL, concurrency-violation translation, and outbox mechanics.

From Session 0004:

- `IMessageDispatcher` in `Domain.Abstractions`, payload is typed `OutboxMessage` (`OutboxId`, `EventId`, `EventType`, `Event` as `IDomainEvent`, `EventMetadata`, `AttemptCount`).
- `InProcessMessageDispatcher` resolves all `IEventHandler<TEvent>` for the event type and invokes them sequentially. Any throw fails dispatch; the outbox row stays pending. All-handlers-succeed marks the row sent.
- `OutboxProcessor` polls every 500ms idle, 5s on exception. `FOR UPDATE SKIP LOCKED`, single transaction per batch, FIFO by `outbox_id`.
- `OutboxRetryPolicy`: exponential with full jitter, base 1s, cap 5min. `MaxAttempts = 10`.
- Quarantine via atomic CTE move with `DELETE ... RETURNING ... INSERT`, preserving `attempt_count` at the SQL level.
- Crash recovery is implicit: "in-flight" is a row lock inside an open transaction; Postgres releases the lock on backend death.
- Composition root: `AddPostgresEventStore`, with `TryAddSingleton<NpgsqlDataSource>` and `TryAddSingleton<JsonSerializerOptions>` so hosts can override.
- ADR 0004 governs engine-specific outbox mechanics, not consumer-side handler resolution. `InProcessMessageDispatcher` lives in `src/Infrastructure/Outbox/` and is engine-agnostic.

## Decisions settled in the Session 0005 planning conversation

These are the design choices the planner conversation closed before drafting this setup. Session 0005 implements them; deviations require a stop-and-surface step.

**1. Projection live tail piggybacks on the existing outbox pipeline.** Projections register `IEventHandler<TEvent>` implementations for the event types they care about. The outbox processor's dispatch drives them at the same FIFO, at-least-once, backoff-and-quarantine semantics the non-projection consumers will share later. Live latency equals outbox polling latency (500ms idle in v1, fine for the no-UI-yet state of the build). The `IEventHandler<TEvent>` contract is uniform across all future event stores; the dispatch driver is per-adapter (outbox processor for Postgres, native catch-up for Kurrent, Streams+Lambda for Dynamo).

**2. `GlobalPosition` lives on `EventEnvelope`.** The Session 0002 deferred decision closes here. `EventEnvelope` gains a `long GlobalPosition` field alongside the existing `EventType`, `Payload`, `Metadata`. `PostgresEventStore.ReadStreamAsync` populates it from the row's `global_position` column. `OutboxMessage` gains the field too; the outbox processor's deserialization path already reads the column at zero additional cost.

**3. `IEventHandler<TEvent>` signature widens to take a typed context wrapper.** Session 0004's `HandleAsync(TEvent, CancellationToken)` becomes `HandleAsync(EventContext<TEvent>, CancellationToken)`:

```csharp
public sealed record EventContext<TEvent>(
    TEvent Event,
    EventMetadata Metadata,
    long GlobalPosition) where TEvent : IDomainEvent;

public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(EventContext<TEvent> context, CancellationToken ct);
}
```

The refactor touches Session 0004's contract one week after it shipped. The cost is small (zero non-test implementations exist today); the benefit is that handlers have everything they need to checkpoint atomically with read-model writes, without a second signature for projection-specific dispatch.

**4. `ICheckpointStore` lives in `Domain.Abstractions`, with transaction-bound advance.**

```csharp
public interface ICheckpointStore
{
    Task<long> GetPositionAsync(string projectionName, CancellationToken ct);

    Task AdvanceAsync(
        string projectionName,
        long position,
        DbTransaction transaction,
        CancellationToken ct);
}
```

`DbTransaction` is in `System.Data.Common`, engine-neutral. `PostgresCheckpointStore` implements against `NpgsqlTransaction` (which is-a `DbTransaction`). The advance is an UPSERT with `GREATEST(existing, incoming)` for idempotent re-delivery. `GetPositionAsync` returns 0 when no row exists.

**5. Read-model storage: two schemas in one PostgreSQL database for v1.** Event-store and read-model sides share the same database. `event_store` schema (existing) holds the events and outbox tables. A new `read_models` schema holds the read-model tables and the projection checkpoints. Transactions span both schemas atomically when needed. The split-database production pattern Ch 13 advocates is reserved for a later session if a concrete need surfaces.

**6. Projections have a lightweight `IProjection` marker.**

```csharp
public interface IProjection
{
    string Name { get; }
}
```

Projection classes implement `IProjection` plus `IEventHandler<TEvent>` per event type they handle. The `Name` property pins the checkpoint key once; handler call sites reference `Name` rather than `nameof(...)`. The marker also gives the replayer and the Phase 9 AdminConsole Projection Status Dashboard a clean enumeration via `services.GetServices<IProjection>()`.

**7. LISTEN/NOTIFY deferred.** Session 0005 ships polling-only dispatch. The deferred Session 0004 outbox-signaling question and PLAN.md Phase 6's stretch-goal LISTEN/NOTIFY trigger continue to sit on the queue with a forward pointer. Reason: no host consumes the outbox yet, so there's no UX cost to 500ms polling latency. The LISTEN/NOTIFY session pairs naturally with either the Workers host's arrival or a UI session where sub-second latency starts mattering for real.

**8. Replayer is engine-agnostic and reflection-driven.** A new `ProjectionReplayer` in `src/Projections/Infrastructure/` takes `IEventStore`, an `IProjection` instance, and a service provider. It reflects on the projection to find all `IEventHandler<TEvent>` interfaces it implements, caches the dispatch table, then iterates `IEventStore.ReadAllAsync(fromPosition, ct)` and invokes the matching handler per envelope. Same handler code as live tail; different driver. Single-projection-scoped dispatch (the caller provides the instance), rather than fan-out through DI.

**9. `IEventStore` gains `ReadAllAsync(long fromPosition, CancellationToken ct)`.** Returns `IAsyncEnumerable<EventEnvelope>` in `global_position` order. Required by the replayer. PLAN.md anticipated this in Phase 2 as `ReadAllFromCheckpointAsync(checkpoint)`; Session 0003 didn't ship it. Closing the gap here. The Postgres implementation uses `NpgsqlDataReader` with `SequentialAccess` and yields envelopes as rows are read. All four future adapters will need an equivalent.

**10. `OrderListProjection` read model: payload-source for business-time fields, metadata-source for system-time fields.** F-0003-26's dual-source sub-question resolves in favor of this convention. `placed_utc` sources from `OrderPlaced.OccurredUtc` (business time, when the user placed the order). `last_updated_utc` sources from `envelope.Metadata.OccurredUtc` (system time, when the projection touched the row). The two values agree today (same wall clock) but are semantically distinct and diverge under back-dating, batch imports, and replay reconstructions.

**11. `customer_name` dropped from `OrderListProjection` v1.** The Order aggregate carries only `CustomerId`. Populating `customer_name` without a cross-context read against a non-existent Customer projection (Phase 4) would require enriching the `OrderPlaced` event payload, which means a Track A change to Ch 9's canonical 4-arg shape and another sweep through Ch 16 test sites. Skip the column for v1. Phase 4's customer aggregates and the `CustomerSummary` projection give us a real source for an enrichment pass later.

**12. Read-model migrations ship through the same `MigrationRunner`.** New file `migrations/0002_initial_read_models.sql` creates the `read_models` schema, `read_models.order_list`, `read_models.projection_checkpoints`, and the two `order_list` indexes. The runner stays in `EventStore.Postgres` for v1 (the location is awkward; eventual move to `Infrastructure.Migrations` or similar is flagged for a future session when a third consumer surfaces).

**13. `IOrderListStore` lives next to `OrderListProjection`, not in `Domain.Abstractions`.** The read-model store interface is tightly coupled to the projection that uses it. It exists so the projection can be unit-tested against an in-memory store; it is not a cross-cutting abstraction. Same convention will apply to the three other projections when they ship.

**14. `Projections.Tests` is a new test project.** Mirrors Session 0003's `Infrastructure.Tests` shape. Duplicates `PostgresFixture` for now (one duplication is fine; a third consumer triggers extraction to a shared `tests/TestInfrastructure/` project). New variant `CreateMigratedDatabaseAsync` applies both `0001_initial_event_store.sql` and `0002_initial_read_models.sql`.

## Session 0005 implementation scope

In the natural commit-slicing order:

1. **`EventEnvelope.GlobalPosition` plus `IEventStore.ReadAllAsync`.** Update `Domain.Abstractions`. `EventEnvelope` gains the field. `IEventStore` gains the streaming read method. `OutboxMessage` gains the field too. Update `PostgresEventStore` to populate `GlobalPosition` in `ReadStreamAsync` and implement `ReadAllAsync` against `NpgsqlDataReader` with `SequentialAccess`. New tests: `PostgresEventStore_ReadAllAsync_Tests` asserting global ordering and resumption from arbitrary positions. Existing tests update where the envelope or outbox message shape is asserted.

2. **`IEventHandler<TEvent>` signature widens to `EventContext<TEvent>`.** Update `Domain.Abstractions`. Add the `EventContext<TEvent>` record. Update `InProcessMessageDispatcher` to construct `EventContext` from `OutboxMessage` and pass it. Update Session 0004's tests (`OutboxProcessorTests`, the `RecordingDispatcher` shape) to the new contract.

3. **`ICheckpointStore` plus `PostgresCheckpointStore`.** Add the interface to `Domain.Abstractions`. Implement against PostgreSQL with the UPSERT-and-GREATEST shape. New tests: `PostgresCheckpointStoreTests` covers first-advance, idempotent re-advance, and the `GREATEST` behavior on out-of-order advances (re-delivery from outbox).

4. **`read_models` schema migration.** New `migrations/0002_initial_read_models.sql` creating the `read_models` schema, `read_models.projection_checkpoints` table, `read_models.order_list` table, and the two `order_list` indexes. The runner needs no code change; it is resource-name-driven and picks up the new file automatically. Verify by extending a `MigrationRunner` test to assert both files apply in order on a fresh database.

5. **`IProjection` marker plus `OrderListProjection` plus `IOrderListStore` plus `PostgresOrderListStore`.** Add the marker to `Domain.Abstractions`. Create `src/Projections/OrderList/` with `OrderListProjection`, `IOrderListStore`, `OrderListRow`. Create `src/Infrastructure/ReadModels.Postgres/` with `PostgresOrderListStore`, `NpgsqlReadModelConnectionFactory`, `ReadModelOptions`, and an `AddReadModels` DI extension. Tests: `OrderListProjectionTests` against an in-memory store double, `PostgresOrderListStoreTests` against the fixture.

6. **`ProjectionReplayer` plus rebuild test.** New `src/Projections/Infrastructure/ProjectionReplayer.cs`. Reflection-driven dispatch table cached per projection instance, then iterates `ReadAllAsync` and invokes per-event handlers. Tests: `ProjectionReplayerTests` for the reflection and iteration logic; `OrderListRebuildTests` for the end-to-end story (append events, drive live, capture state, truncate, replay, assert state matches).

7. **Composition root: `AddReadModels` extension and projection registration.** New `AddReadModels(this IServiceCollection, Action<ReadModelOptions>)` extension in `ReadModels.Postgres`. New `ReadModelOptions.ConnectionString`. New `IReadModelConnectionFactory` and `NpgsqlReadModelConnectionFactory`. DI registers `ICheckpointStore`, `IOrderListStore`, and the projection itself as both `IProjection` and the relevant `IEventHandler<TEvent>` typed registrations (forwarded from a single `OrderListProjection` singleton). New `ServiceCollectionExtensions_AddReadModels_Tests` for DI resolution.

**Done when:**

- All 52 existing tests continue to pass after the `IEventHandler` refactor.
- The `OrderListProjection` processes the full Order lifecycle correctly via live dispatch through `InProcessMessageDispatcher`.
- The rebuild test (e.g., `OrderListRebuildTests.replay_from_zero_produces_identical_read_model_state_as_live_dispatch`) passes.
- The checkpoint advances atomically with the read-model write in each handler.
- `dotnet build` and `dotnet test` are green; CI is green on push.

## Project layout after Session 0005

```
src/
  Domain.Abstractions/
    (existing types)
    EventContext.cs                   // new
    EventEnvelope.cs                  // GlobalPosition field added
    IEventStore.cs                    // ReadAllAsync added
    IEventHandler.cs                  // signature widened
    IProjection.cs                    // new marker
    ICheckpointStore.cs               // new
    OutboxMessage.cs                  // GlobalPosition field added
  Projections/
    Infrastructure/
      ProjectionReplayer.cs           // new
    OrderList/
      OrderListProjection.cs          // new
      IOrderListStore.cs              // new
      OrderListRow.cs                 // new
  Infrastructure/
    ReadModels.Postgres/              // new project
      PostgresOrderListStore.cs
      PostgresCheckpointStore.cs
      NpgsqlReadModelConnectionFactory.cs
      IReadModelConnectionFactory.cs
      ReadModelOptions.cs
      ServiceCollectionExtensions.cs  // AddReadModels
    EventStore.Postgres/
      PostgresEventStore.cs           // ReadAllAsync added; envelope construction updated
      OutboxProcessor.cs              // dispatch path constructs EventContext
      (existing files unchanged otherwise)
    Outbox/
      InProcessMessageDispatcher.cs   // signature change to EventContext<TEvent>
migrations/
  0001_initial_event_store.sql        // existing
  0002_initial_read_models.sql        // new
tests/
  Projections.Tests/                  // new project
    OrderListProjectionTests.cs
    PostgresOrderListStoreTests.cs
    PostgresCheckpointStoreTests.cs
    ProjectionReplayerTests.cs
    OrderListRebuildTests.cs
    PostgresFixture.cs                // duplicated from Infrastructure.Tests
    ServiceCollectionExtensions_AddReadModels_Tests.cs
  Infrastructure.Tests/
    Postgres/
      PostgresEventStore_ReadAllAsync_Tests.cs   // new
      (existing tests updated for the IEventHandler signature)
```

## Test count projection

| Checkpoint | Domain.Tests | Infrastructure.Tests | Projections.Tests | Total |
| --- | --- | --- | --- | --- |
| Session 0004 baseline | 22 | 30 | 0 | 52 |
| Session 0005 projection | 22 | 35 | 25 | 82 |

`Projections.Tests` planned methods: 8 `OrderListProjectionTests`, 5 `PostgresOrderListStoreTests`, 4 `PostgresCheckpointStoreTests`, 4 `ProjectionReplayerTests`, 3 `OrderListRebuildTests`, 1 `ServiceCollectionExtensions_AddReadModels_Tests`. `Infrastructure.Tests` delta: 5 new methods for `PostgresEventStore.ReadAllAsync`; existing tests keep their counts after shape updates.

The xUnit case count may diverge from method count if any test method is written as a `[Theory]`. Final counts land in the session log per the Session 0004 convention (planned methods, actual methods, actual xUnit cases).

## Deferred items and forward references

Carried forward from Session 0004 and earlier, plus new items surfaced in this planning conversation:

1. **LISTEN/NOTIFY trigger.** Defer to the Workers host session or the first UI session where sub-second latency matters. The deferred Session 0004 outbox-signaling question lives here too.
2. **Per-event batching for catch-up performance.** Ch 13's "naive projections suffer most" advice. Not needed at v1 read loads. Flag for a future session if rebuild duration becomes a real concern.
3. **Blue-green and parallel/shadow rebuild patterns.** Ch 13 documents three rebuild variants; v1 ships in-place rebuild only. The other two can land in a later phase if the test suite or AdminConsole earns them.
4. **Per-consumer outbox semantics.** v1's outbox marks `sent_utc` when all handlers agree. Future broker forwarding (post-v1) needs either per-consumer outbox tracking or a separate "broker sent up to position" checkpoint. Flagged for the broker session whenever it arrives.
5. **`MigrationRunner` location.** Lives in `EventStore.Postgres` for v1. The location is awkward once a second consumer (read-model migrations) joins. Eventual move to `Infrastructure.Migrations` when a third consumer surfaces.
6. **`PostgresFixture` extraction.** Duplicated between `Infrastructure.Tests` and `Projections.Tests` in Session 0005. A third project consuming it triggers extraction to `tests/TestInfrastructure/`.
7. **`customer_name` on `OrderListProjection`.** Awaits Phase 4's customer aggregates and `CustomerSummary` projection. Enrichment pass adds the column then.
8. **Application command pipeline and real metadata stamping.** Session 0001's flag 5 stays open. `EventStoreRepository` fills metadata with `Guid.Empty` placeholders and `"Domain"` source. Phase 2's command pipeline replaces the placeholders; nothing in Session 0005 needs to touch this. Projections reading `envelope.Metadata.OccurredUtc` may see the placeholder until the command pipeline ships; the test suite seeds metadata explicitly to avoid this.
9. **Snapshot infrastructure.** PLAN.md Phase 1 listed `ISnapshotStore` as a Phase 1 deliverable; reality is Phase 12. PLAN.md reconciliation owed in Phase 14 per F-0001-A.
10. **xUnit v3 cancellation-token sweep.** Deferred until Stryker.NET issue #3117 unblocks v3 (ADR 0003).
11. **Read-model split to a separate database.** v1 uses two schemas in one database. Future session may split the read side into its own database if scaling, restore-from-events, or operational isolation surfaces a concrete need.

## Track A flag candidates this session generates

The implementation pre-empts F-0003-26 by picking option (a) from that flag's entry: one dispatch signature, propagate through Ch 13. Additional flags this session is likely to generate, pre-recorded so the session log captures them without re-derivation. Final IDs assigned at session-log time.

1. **`IEventHandler<TEvent>` signature.** Ch 13's early `OrderListProjection` uses `HandleAsync(EventEnvelope envelope, CancellationToken ct)` with `envelope.Payload` switch. Deep-dive projections use `HandleAsync(TEvent evt, EventMetadata meta, CancellationToken ct)`. Implementation ships `HandleAsync(EventContext<TEvent> context, CancellationToken ct)` with typed payload, metadata, and `GlobalPosition` on the context wrapper. Chapter needs normalization across early and deep-dive sites. Co-resolves with F-0003-26.

2. **`ICheckpointStore` shape.** Ch 13 uses both `SaveAsync(name, position, transaction, ct)` (early code, 4-arg with transaction) and `AdvanceAsync(name, position, ct)` (deep-dive, 3-arg without transaction). Implementation ships `GetPositionAsync(name, ct)` plus `AdvanceAsync(name, position, transaction, ct)` (4-arg with `DbTransaction`). Chapter unifies on the implementation shape. Co-resolves with F-0003-26.

3. **`GlobalPosition` placement on `EventEnvelope`.** Settles the placement question Ch 13 leaves open (`envelope.GlobalSequence` in early code, `meta.GlobalPosition` in deep-dive). Implementation puts `GlobalPosition` on `EventEnvelope`. F-0003-03 already normalized the naming across chapters to `GlobalPosition`; this flag closes the placement question for projections.

4. **`IProjection` marker interface.** Ch 13's early code shows `IProjection` with both a `Name` property and a `HandleAsync(EventEnvelope, CancellationToken)` method on the same interface. Implementation splits the two roles: `IProjection` is marker-only (`Name` property), and per-event dispatch comes through `IEventHandler<TEvent>`. Chapter should split the roles or note the implementation split.

5. **`customer_name` in `OpsOrderListProjection`.** Ch 13's deep-dive shows the column denormalized from `OrderPlaced.CustomerName`. Reference implementation drops the column for v1 (Order aggregate carries only `CustomerId`; Phase 4 enrichment pass adds it). Chapter could acknowledge the staging in a footnote or leave the deep-dive as aspirational.

6. **Read-model storage location.** Ch 13 prose advocates separate-database storage for read models; the reference implementation uses two schemas in one database for v1 with the split deferred to a later session if a concrete need surfaces. Chapter could acknowledge the implementation choice and the production-pattern delta.

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

> Please re-read CLAUDE.md, docs/PLAN.md, docs/CLAUDE_CODE_PREAMBLE.md, and docs/ai-writing-style-source.txt. Then re-read `docs/sessions/0005-weeks5-6-first-projection-setup.md` for the design record this session implements. Then summarize back to me: (1) the work items already complete based on what is in the repo, and (2) the work items still remaining for Session 0005. After I confirm the summary is right, we will start on commit 1.

Session 0005 runs through the seven-commit slice above. Each commit is a logical unit with its own tests passing before the next commit starts. Propose-before-writing applies per CLAUDE_CODE_PREAMBLE.md. Stop and surface if the agreed design turns out to be incorrect during execution.

Track A flags discovered during execution land in the session log under "Cross-track flags." The session log lives at `docs/sessions/0005-weeks5-6-first-projection.md` post-execution; this setup document archives at `docs/sessions/0005-weeks5-6-first-projection-setup.md`.
