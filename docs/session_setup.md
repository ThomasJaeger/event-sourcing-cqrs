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
  - Session 0004 (design committed, execution pending): `OutboxProcessor` plus `IMessageDispatcher` plus `AddPostgresEventStore` composition root
- Weeks 5-6: first projection and the read side (Session 0005, next Track B planning topic)

PLAN.md's phase numbering does not match the six-week plan's week numbering. Where the two disagree, the six-week plan is in effect through mid-June. PLAN.md reconciliation happens in Phase 14 or sooner if the divergence grows.

The repo-wide rules for Claude Code live in `CLAUDE.md`. My writing style rules live in `docs/ai-writing-style-source.txt` and apply to anything Claude produces (chat prose, code comments, ADRs, commit messages, PR descriptions).

## Where the build stands now

Sessions 0001, 0002, and 0003 are shipped. Session 0004's design record is committed but Track C has not yet executed it.

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

### Session 0004 (design committed, execution pending)

The design record is at `docs/sessions/0004-weeks3-4-outbox-processor.md`. It captures eight design decisions made in Track B (dispatch trigger, read query and lock mode, backoff schedule, quarantine path, crash recovery, `IMessageDispatcher` interface, test plan, composition root) plus eight cross-track flags accumulating against Chapter 8's Publication of Events section.

Headline decisions: polling-only dispatch (no `LISTEN/NOTIFY` until Phase 6); `FOR UPDATE SKIP LOCKED` for defense-in-depth; exponential backoff with full jitter, base 1s, cap 5min; atomic CTE-based move-to-quarantine that structurally carries `attempt_count` and `last_error`; row-lock-based crash recovery with no in-flight column needed; `IMessageDispatcher` plus `IEventHandler<TEvent>` in `Domain.Abstractions` with a typed `OutboxMessage` envelope; `INpgsqlConnectionFactory` PostgreSQL-specific in the adapter (not in Domain.Abstractions); `AddPostgresEventStore` extension method as the composition-root surface.

Planned test count after Session 0004 execution: 22 Domain + 41 Infrastructure = 63 total. Eleven new tests (8 integration, 2 retry-policy unit, 1 DI-resolution).

Track C will execute Session 0004 against the design record. Updates to the design record happen in place per the single-file convention.

## What is not yet in place

- **Session 0004 execution by Track C.** The eleven tests, the `OutboxProcessor`, the `OutboxRetryPolicy`, the `IMessageDispatcher` and `InProcessMessageDispatcher`, the `IEventHandler<TEvent>` abstraction, the `INpgsqlConnectionFactory`, the `OutboxProcessorOptions`, the `AddPostgresEventStore` extension. All scoped in the design record.

- **Workers host.** No `Program.cs` yet consumes `AddPostgresEventStore`. The first host probably lands alongside the Phase 2 Application command pipeline session or the Web/Api session in Phase 7, depending on what the foundation plan needs first. The embedded migration-runner call flagged in Session 0002 (`MigrationRunner.RunPendingAsync` before binding ports) lands in that same host-introduction session.

- **First projection (Session 0005).** Weeks 5-6 of the six-week plan. `OrderListProjection` is the canonical first one given the Order aggregate is already in place. The projection-trigger session for `LISTEN/NOTIFY` lives alongside this work and is where the deferred Session-0004 question (LISTEN/NOTIFY for the outbox processor too?) gets answered.

- **Application layer.** Empty project shell ready. Commands, queries, middleware, `ICommandContext` arrive in Phase 2 with the command pipeline. Real correlation/causation/actor metadata flows from there into `EventStoreRepository`'s envelope construction.

- **`EventTypeRegistry` cleaner two-phase registration.** Per-aggregate-module `IEventTypeProvider` contributions composed by a single registry factory. Lands alongside the first host introduction.

- **SQL Server adapter.** Parallel hand-rolled relational implementation in `src/Infrastructure/EventStore.SqlServer/`. Self-contained per ADR 0004. Later in Phase 2 after the PostgreSQL trio (Sessions 0002, 0003, 0004) is fully shipped.

- **KurrentDB and DynamoDB adapters.** Phases 10 and 11 of PLAN.md.

- **Snapshot infrastructure.** Phase 12.

## What the next Track B session is for

Session 0005: planning for the first projection.

The expected scope is `OrderListProjection` plus the projection infrastructure that supports it — `IProjection` or equivalent abstraction, checkpoint store, the polling consumer that drives the projection from the events table forward by `global_position`, and the test pattern for projection rebuilds. The trigger mechanism question (polling versus `LISTEN/NOTIFY`) is open and is the natural Session 0005 design topic; it also revisits the deferred Session-0004 question about whether the outbox processor benefits from the same signaling.

If Track C surfaces a design question during Session 0004 execution that needs Track B input rather than a `CLAUDE.md`-grounded judgment call, this same `session_setup.md` is the entry point for that follow-up Track B touch-up conversation; the responsible session just states its specific task instead of opening Session 0005 scope.

## Cross-track flags pending

Eight flags accumulated in Session 0004's design record, all against Chapter 8's Publication of Events section. The next Track A session should be informed of these so the next Chapter 8 pass picks them up. Summary:

1. Chapter 8's `OutboxProcessor` code is polling-only with no `LISTEN/NOTIFY` and no backoff column; implementation adds `next_attempt_at` and `last_error`.
2. Chapter 8's processor reads pending rows without a lock hint; implementation adds `FOR UPDATE SKIP LOCKED`.
3. Chapter 8 commits to "exponential backoff" in prose but doesn't realize it in code; implementation does via `OutboxRetryPolicy` (base 1s, cap 5min, full jitter).
4. Chapter 8's `QuarantineAsync` is opaque; implementation realizes it as an atomic `DELETE ... RETURNING ... INSERT` CTE.
5. Chapter 8 describes timeout-threshold startup recovery; implementation relies on transaction-scoped row locks and needs no recovery code.
6. Chapter 8 references `IMessageDispatcher` and `IEventHandler<TEvent>` without defining either; implementation defines both in `Domain.Abstractions`.
7. Chapter 8's processor uses wall-clock `DateTime` implicitly; implementation injects `TimeProvider` and a jitter source via `OutboxProcessorOptions`.
8. Chapter 8 shows no composition-root wiring; implementation ships `AddPostgresEventStore` extension with adapter-specific `INpgsqlConnectionFactory`.

Full text in `docs/sessions/0004-weeks3-4-outbox-processor.md`. Also accumulated from earlier sessions are the 25 Track A flags in Session 0003's log and the 10 Track A flags in Session 0002's log. None are resolved yet on the book side.
