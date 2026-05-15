---

## Track context

This document is the entry point for **Track B** of a three-track project: **code planning** in Claude.ai. The other tracks are:

- **Track A** — Claude.ai sessions for **book content**. Entry point: `HANDOFF.md` in the book repo at `~/Documents/GitHub/event-sourcing-cqrs-book/`.
- **Track C** — Claude Code in the terminal for **code execution**. Entry point: `CLAUDE.md` + `docs/PLAN.md` + `docs/ai-writing-style-source.txt` in this repo.

When work in one track affects another, the originating session adds a **Cross-track flag** entry to its `docs/sessions/NNNN-<description>.md` log. The next time the affected track is opened, the human (Thomas) mentions the flag at the start of the session.

The .NET 8 → .NET 10 decision was the canonical cross-track example: ADR 0001 in this repo committed to .NET 10, which required the manuscript in the book repo to update Part 4 Tech Stack and the Reference Implementation references in Part 5. Thomas routed the manuscript change to Track A and the ADR status update back to this track when complete.

For a quick-reference routing card see `WHICH_SESSION.md` at the root of the book repo.

---


# Setup for the next session

I am continuing work on a project we have been collaborating on. Here is the full context.

## The project

I am writing a book titled *Event Sourcing & CQRS: A Comprehensive and Practical Guide to Deeper Insights in Your Software Solutions*. The manuscript is complete at 439 pages, 18 chapters, 72 custom diagrams, plus a Part 4 reference implementation specification and a Part 5 resources appendix.

I am preparing to submit a proposal to Pearson/Addison-Wesley for the Vaughn Vernon Signature Series. The proposal package is complete in draft form: table of contents, competitive coverage grid, positioning statement, chapter-by-chapter summaries, two sample chapters (Chapter 11 Event Versioning and Chapter 13 CQRS), author bio, marketing plan, supplementary materials description. The submission is held until the reference implementation is done. Path 1: book and code match end to end before the proposal goes to Pearson.

## About me

Thomas Jaeger. .NET architect and developer. The book is the result of multiple years of practical event-sourcing work plus a deliberate effort over the past several months to write the comprehensive practitioner's guide I wanted to read myself when I started.

## Tech stack

.NET 10 with C# 14. xUnit v2 with FluentAssertions v7 for tests (ADRs 0002 and 0003 record the version pins; v3 in both cases is deferred until the ecosystem catches up). FsCheck for property-based tests. Stryker.NET for mutation testing on `Domain`. Testcontainers for PostgreSQL and KurrentDB integration. LocalStack for DynamoDB integration. PostgreSQL 16 for the event store and read models.

Four event store adapters as first-class peers behind `IEventStore`: hand-rolled PostgreSQL (Npgsql), hand-rolled SQL Server (Microsoft.Data.SqlClient), KurrentDB (gRPC), DynamoDB (conditional writes). Per ADR 0004 each adapter is self-contained — no shared base class, no shared SQL composer, each adapter owns its full surface including outbox mechanics.

Five aggregates across four bounded contexts. Sales: Order. Fulfillment: Inventory, Shipment. Billing: Payment. Customer Support: read-only context. The Order aggregate is built and tested as of Session 0001. The other three ship in Phase 4.

Two process managers, both event-sourced themselves. OrderFulfillmentProcessManager covers the four-branch saga from Chapter 10 with all compensation paths. ReturnProcessManager is the smaller second example. Both ship in Phase 5.

Four projections. OrderListProjection, OrderDetailProjection, CustomerSummaryProjection, InventoryDashboardProjection. Read models live in PostgreSQL with a mix of relational tables and JSONB columns. First projection ships in Weeks 5-6 of the foundation plan.

Blazor Server UI plus ASP.NET Core minimal API exposing the same operations as JSON. Tailwind for styling. SignalR for live dashboards.

Hexagonal architecture. Domain at the center with no I/O dependencies. Application depends on Domain and Domain.Abstractions only. Infrastructure projects implement the abstractions Domain.Abstractions declares. Hosts (Web, Api, Workers, AdminConsole) depend on Application.

## The build plan

`docs/PLAN.md` in the repo describes the 14-phase pacing across 28 weeks. That document remains the long-arc plan and the eventual book reconciliation reference.

The reconciled six-week foundation plan (master setup, May 8, 2026) governs the current work window from May through mid-June and reorders early work to land a runnable Order workflow as quickly as possible:

- Weeks 1-2: in-memory foundation (Session 0001, shipped)
- Weeks 3-4: PostgreSQL adapter and outbox, three sessions
  - Session 0002 (shipped): initial migration plus `MigrationRunner` plus CLI
  - Session 0003 (shipped): PostgreSQL `IEventStore` adapter with `AppendAsync`, `ReadStreamAsync`, `EventTypeRegistry`
  - Session 0004 (shipped): `OutboxProcessor` plus `IMessageDispatcher` plus `AddPostgresEventStore` composition root
- Weeks 5-6: first projection and the read side (Session 0005, shipped): `EventEnvelope.GlobalPosition` plus `IEventStore.ReadAllAsync`, `EventContext<TEvent>`, `ICheckpointStore`, `OrderListProjection`, `ProjectionReplayer`, `AddReadModels`

PLAN.md's phase numbering does not match the six-week plan's week numbering. Where the two disagree, the six-week plan is in effect through mid-June. PLAN.md reconciliation happens in Phase 14 or sooner if the divergence grows.

The repo-wide rules for Claude Code live in `CLAUDE.md`. My writing style rules live in `docs/ai-writing-style-source.txt` and apply to anything Claude produces (chat prose, code comments, ADRs, commit messages, PR descriptions).

## Where the build stands now

Sessions 0001 through 0005 are shipped.

### Session 0001 (shipped)

In-memory event store, Order aggregate with five commands and corresponding events, command handlers, Given-When-Then aggregate test pattern. The complete in-memory foundation against which the Postgres adapter is the first real I/O.

### Session 0002 (shipped)

The first migration file `migrations/0001_initial_event_store.sql` plus the `MigrationRunner` plus the `EventStore.Postgres.Cli` console host.

PostgreSQL 16 schema in the `event_store` namespace. Four tables: `events`, `outbox`, `outbox_quarantine`, `schema_migrations`. IDENTITY columns, STORED generated columns for `correlation_id` (events and outbox) and `causation_id` (events only), JSONB for payload and metadata. Constraint and index names follow `pk_<table>`, `uq_<table>_<column-or-tuple>`, `ix_<table>_<column-or-purpose>` with the `event_store.` schema prefix carrying the disambiguation context. Partial index `ix_outbox_pending ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` orders pending rows in FIFO sequence for the OutboxProcessor.

`MigrationRunner` with one public method (`RunPendingAsync`), `MigrationRunnerOptions`, `MigrationChecksumMismatchException`. Embedded resources expose `/migrations/*.sql` under the `EventStore.Postgres.Migrations.` resource prefix. `pg_advisory_lock` key `0x4553_5243_515F_4D52L` (ASCII `ESRCQ_MR`) guards the runner across all per-migration transactions. SHA-256 checksums over raw embedded bytes detect post-application edits. Dry-run mode prints the pending list without acquiring the lock or running DDL but still throws on a checksum mismatch.

`EventStore.Postgres.Cli` console project: single `Program.cs` parsing `migrate` and optional `--dry-run`, reading `EVENT_STORE_CONNECTION_STRING`, invoking the runner. Exit codes 0 success, 1 runner failure, 64 usage, 78 missing env var.

`.gitattributes`: `* text=auto eol=lf` with binary carve-outs. SHA-256 checksums on embedded migration files require stable bytes across Windows and Linux/macOS checkouts.

32 tests passing (22 Domain.Tests, 10 Infrastructure.Tests).

### Session 0003 (shipped)

PostgreSQL `IEventStore` adapter implementation in `EventStore.Postgres`. `PostgresEventStore` with `AppendAsync(streamId, expectedVersion, events, ct)` and `ReadStreamAsync(streamId, fromVersion, ct)`. Events-plus-outbox atomic write in one `NpgsqlTransaction`. Optimistic concurrency mapped to the `uq_events_stream_version` unique constraint, surfaced as `ConcurrencyException` with two-argument constructor `(expectedVersion, actualVersion)`.

`EventTypeRegistry` introduced in `Infrastructure/EventStore.Postgres` as the typed-deserialization resolver. Manual registration today via `Register<TEvent>()`; the registry moves to `Infrastructure/Versioning` in Phase 12 alongside the upcaster pipeline. The Phase-12 schema-registry stub builds on this seed.

20 new tests in Session 0003: 8 `EventTypeRegistryTests`, 7 `PostgresEventStore_AppendAsync_Tests`, 5 `PostgresEventStore_ReadStreamAsync_Tests`. Test count after Session 0003: 22 Domain + 30 Infrastructure = 52 total. Container startup remains a one-time cost amortized across test classes sharing `PostgresFixture` via `IClassFixture` per Session 0002's expectation; cold-cache wall time stayed within the linear-growth envelope.

`EventStore.Postgres.csproj` gained a `ProjectReference` to `Domain.Abstractions` (the `EventTypeRegistry`'s `Register<TEvent> where TEvent : IDomainEvent` constraint surfaced the need). This was the one deviation from Session 0003's pre-execution design.

### Session 0004 (shipped)

The design record is at `docs/sessions/0004-weeks3-4-outbox-processor.md`. It captures eight design decisions made in Track B (dispatch trigger, read query and lock mode, backoff schedule, quarantine path, crash recovery, `IMessageDispatcher` interface, test plan, composition root) plus eight cross-track flags accumulating against Chapter 8's Publication of Events section.

Headline decisions: polling-only dispatch (no `LISTEN/NOTIFY` until Phase 6); `FOR UPDATE SKIP LOCKED` for defense-in-depth; exponential backoff with full jitter, base 1s, cap 5min; atomic CTE-based move-to-quarantine that structurally carries `attempt_count` and `last_error`; row-lock-based crash recovery with no in-flight column needed; `IMessageDispatcher` plus `IEventHandler<TEvent>` in `Domain.Abstractions` with a typed `OutboxMessage` envelope; `INpgsqlConnectionFactory` PostgreSQL-specific in the adapter (not in Domain.Abstractions); `AddPostgresEventStore` extension method as the composition-root surface.

Planned test count after Session 0004 execution: 22 Domain + 41 Infrastructure = 63 total. Eleven new tests (8 integration, 2 retry-policy unit, 1 DI-resolution).

Track C executed Session 0004 against the design record. Updates to the design record happen in place per the single-file convention.

### Session 0005 (shipped)

`OrderListProjection` and the read side. Six commits land the slice end to end: `EventEnvelope.GlobalPosition` plus `IEventStore.ReadAllAsync` on both adapters, the `EventContext<TEvent>` handler signature, `ICheckpointStore` plus `PostgresCheckpointStore`, the `ReadModels.Postgres` project, `IProjection` and the `Projections` project, `OrderListProjection` with `IOrderListStore` plus `IOrderListUnitOfWork`, `PostgresOrderListStore`, the `ProjectionReplayer`, and the `AddReadModels` composition root. Migrations `0002_add_outbox_global_position.sql`, `0003_initial_read_models.sql`, and `0004_add_order_list_read_model.sql` land alongside.

Headline decisions: `IProjection` is marker-only with per-event dispatch through `IEventHandler<TEvent>` (invariant, not `<in TEvent>`, by compiler constraint); `ICheckpointStore.AdvanceAsync` is transaction-bound and the projection write path is a unit of work (`IOrderListUnitOfWork.CommitAsync`); `EventContext<TEvent>` carries typed payload, metadata, and `GlobalPosition` on one wrapper; `OrderListProjection` handles `OrderPlaced`, `OrderShipped`, `OrderCancelled` only (`OrderPlaced` carries the final `Total`); `Money` on the read model is two columns (`total_amount NUMERIC(18,4)`, `total_currency TEXT`); `ProjectionReplayer` is single-projection-scoped (constructor-injected `IProjection`); `AddReadModels` builds the read-model `NpgsqlDataSource` inside the `IReadModelConnectionFactory` factory delegate, not via a shared `TryAddSingleton<NpgsqlDataSource>` with `AddPostgresEventStore`.

111 xUnit cases at the end of Session 0005: 22 Domain.Tests, 63 Infrastructure.Tests, 26 Projections.Tests. The design record, deviations, and per-commit deltas live at `docs/sessions/0005-weeks5-6-first-projection.md`.

## What is not yet in place

- **Workers host.** Both `AddPostgresEventStore` and `AddReadModels` are wired but unconsumed: no `Program.cs` calls them, nothing starts the `OutboxProcessor` as a `BackgroundService`, and `OrderListProjection`'s live tail never runs outside the rebuild test. The Workers host is the consumer that changes that. It is also where the deferred `LISTEN/NOTIFY` trigger pairs naturally with the first latency-sensitive consumer, where the read-model `NpgsqlDataSource` disposal-lifetime question (Session 0005's commit-6 deviation) gets settled, and where the embedded `MigrationRunner.RunPendingAsync` call flagged in Session 0002 lands.

- **Application layer.** Empty project shell ready. Commands, queries, middleware, and `ICommandContext` arrive with the command pipeline. Real correlation/causation/actor metadata flows from there into `EventStoreRepository`'s envelope construction.

- **`EventTypeRegistry` cleaner two-phase registration.** Per-aggregate-module `IEventTypeProvider` contributions composed by a single registry factory. Lands alongside the first host introduction.

- **Additional aggregates.** Inventory and Shipment (Fulfillment), Payment (Billing). Phase 4 of PLAN.md.

- **Process managers.** `OrderFulfillmentProcessManager` (the four-branch saga with all compensation paths) and `ReturnProcessManager`. Both event-sourced. Phase 5 of PLAN.md.

- **Additional projections.** `OrderDetailProjection`, `CustomerSummaryProjection`, `InventoryDashboardProjection`. The second projection forces the projection-registration-helper question; `AddReadModels` hand-writes five registrations for one projection today.

- **UI and API hosts.** Blazor Server Web host plus minimal-API host. Phase 7.

- **SQL Server adapter.** Parallel hand-rolled relational implementation in `src/Infrastructure/EventStore.SqlServer/`. Self-contained per ADR 0004. Later in Phase 2 after the PostgreSQL slice is fully consumed.

- **KurrentDB and DynamoDB adapters.** Phases 10 and 11 of PLAN.md.

- **Snapshot infrastructure.** Phase 12.

## What the next Track B session is for

The next planner conversation chooses the topic. The Workers host is the natural follow-on per the Session 0005 log's "Notes for next sessions": it consumes both `AddPostgresEventStore` and `AddReadModels`, runs `OutboxProcessor` as a `BackgroundService`, runs the live tail of `OrderListProjection`, calls `MigrationRunner.RunPendingAsync` before binding ports, and is the right pressure point for the deferred `LISTEN/NOTIFY` signaling and the read-model `NpgsqlDataSource` disposal-lifetime question.

## Cross-track flags pending

The consolidated index lives in the book repo at `~/Documents/GitHub/event-sourcing-cqrs-book/docs/sessions/cross-track-flags-summary.md`. As of book-repo commit `b354d1d`, the index covers 48 flags across the five shipped sessions (0001 through 0005). Chapter 13 is the post-arc concentration point with a well-scoped reconciliation pass owed against the dispatch-signature, checkpoint, and unit-of-work flags Session 0005 added; see F-0003-26 in the index for the cluster scope. Per-session flag detail lives in each session log under "Cross-track flags (Track A)"; this file no longer enumerates them.
