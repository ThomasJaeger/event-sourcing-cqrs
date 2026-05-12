---

## Track context

This document is the entry point for **Track B** of a three-track project: **code planning** in Claude.ai. The other tracks are:

- **Track A** — Claude.ai sessions for **book content**. Entry point: `HANDOFF.md` in the book repo at `~/Documents/GitHub/event-sourcing-cqrs-book/`.
- **Track C** — Claude Code in the terminal for **code execution**. Entry point: `CLAUDE.md` + `docs/PLAN.md` + `docs/rules.txt` in this repo.

When work in one track affects another, the originating session adds a **Cross-track flag** entry to its `docs/sessions/NNNN-<description>.md` log. The next time the affected track is opened, the human (Thomas) mentions the flag at the start of the session.

The .NET 8 → .NET 10 decision was the canonical cross-track example: ADR 0001 in this repo committed to .NET 10, which required the manuscript in the book repo to update Part 4 Tech Stack and the Reference Implementation references in Part 5. Thomas routed the manuscript change to Track A and the ADR status update back to this track when complete.

For a quick-reference routing card see `WHICH_SESSION.md` at the root of the book repo.

---


# Session Setup: Next Session

This document is the setup message for the next conversation with Claude on the web. Copy the body of this file (everything below the horizontal rule) and paste it as the first message of a new conversation on claude.ai.

This file lives in the repository as a running record of session boundaries. Each session that produces meaningful work should leave behind an updated copy of this file describing where the build was when that session ended and what the next session is expected to start on. Over the six-month build, this folder becomes a history of how the implementation actually progressed.

Save this version as `docs/sessions/0003-weeks3-4-postgres-adapter-impl.md` after the next session begins, and create a new `session_setup.md` for the session after that.

---

# Setup for the next session

I am continuing work on a project we have been collaborating on. Here is the full context.

## The project

I am writing a book titled *Event Sourcing & CQRS: A Comprehensive and Practical Guide to Deeper Insights in Your Software Solutions*. The manuscript is complete at 439 pages, 18 chapters, 72 custom diagrams, plus a Part 4 reference implementation specification and a Part 5 resources appendix.

I am preparing to submit a proposal to Pearson/Addison-Wesley for the Vaughn Vernon Signature Series. The proposal package is complete in draft form: table of contents, competitive coverage grid, positioning statement, chapter-by-chapter summaries, two sample chapters (Chapter 11 Event Versioning and Chapter 13 CQRS), author bio, marketing plan, supplementary materials description. The submission is held until the reference implementation is done. Path 1: book and code match end to end before the proposal goes to Pearson.

## About me

Thomas Jaeger. Principal Architect with four decades in software since 1991. I run Legacy to Modern LLC, a one-year-old consultancy focused on modernizing legacy systems into event-sourced cloud-native architectures. The consultancy has its first paying customer; I work it part-time alongside a day job and want to take it full-time once the book gives the practice enough credibility to drive consistent inbound leads.

Earlier in my career I was a Senior Cloud Application Architect at a major cloud provider, where I introduced DDD, CQRS, and Event Sourcing to enterprise engineering teams across financial services, retail, telecom, and media. I led executive-level Event Storming workshops there and have facilitated dozens of similar sessions since.

I work in C# and .NET. I produce the YouTube channel *Creating Great Software* (1,200 subscribers, top video on Hexagonal Architecture at 25,000 views), write at thomasjaeger.wordpress.com, and share code at github.com/ThomasJaeger.

I attended a presentation by Greg Young while at the cloud provider. That is the extent of my contact with the Event Sourcing community's headline figures so far.

## The reference implementation

Public repo at github.com/ThomasJaeger/event-sourcing-cqrs. MIT licensed. Phase 1 (Weeks 1-2 in-memory foundation) and the first of three sessions in Phase 2 (Weeks 3-4 PostgreSQL adapter) are complete. CI green on every push.

Built on .NET 10 LTS, C# 14. The book commits to three event-store implementations as first-class peers behind a common abstraction:

- Hand-rolled PostgreSQL (the relational path)
- KurrentDB via gRPC client (the specialized path)
- DynamoDB via LocalStack (the managed-cloud path)

Switching between them is a configuration change, not a code change.

Five aggregates across four bounded contexts. Sales: Order. Fulfillment: Inventory, Shipment. Billing: Payment. Customer Support: read-only context. The Order aggregate is built and tested as of Session 0001. The other three ship in Phase 4.

Two process managers, both event-sourced themselves. OrderFulfillmentProcessManager covers the four-branch saga from Chapter 10 with all compensation paths. ReturnProcessManager is the smaller second example. Both ship in Phase 5.

Four projections. OrderListProjection, OrderDetailProjection, CustomerSummaryProjection, InventoryDashboardProjection. Read models live in PostgreSQL with a mix of relational tables and JSONB columns. Ship in Weeks 5-6 of the foundation plan.

Blazor Server UI plus ASP.NET Core minimal API exposing the same operations as JSON. Tailwind for styling. SignalR for live dashboards.

Hexagonal architecture. Domain at the center with no I/O dependencies. Application depends on Domain and Domain.Abstractions only. Infrastructure projects implement the abstractions Domain.Abstractions declares. Hosts (Web, Api, Workers, AdminConsole) depend on Application.

Test stack: xUnit v2, FluentAssertions v7, FsCheck for property-based tests, Stryker.NET for mutation testing on Domain, Testcontainers for PostgreSQL and KurrentDB integration, LocalStack for DynamoDB integration.

## The build plan

`docs/PLAN.md` in the repo describes the 14-phase pacing across 28 weeks. That document remains the long-arc plan and the eventual book reconciliation reference.

The reconciled six-week foundation plan (master setup, May 8, 2026) governs the current work window from May through mid-June and reorders early work to land a runnable Order workflow as quickly as possible:

- Weeks 1-2: in-memory foundation (Session 0001, shipped)
- Weeks 3-4: PostgreSQL adapter and outbox, three sessions
  - Session 0002 (shipped): initial migration plus `MigrationRunner` plus CLI
  - Session 0003 (this session's planning target): PostgreSQL `IEventStore` adapter
  - Session 0004: `OutboxProcessor`
- Weeks 5-6: first projection and the read side

PLAN.md's phase numbering does not match the six-week plan's week numbering. Where the two disagree, the six-week plan is in effect through mid-June. PLAN.md reconciliation happens in Phase 14 or sooner if the divergence grows.

The repo-wide rules for Claude Code live in `CLAUDE.md`. My writing style rules live in `docs/rules.txt` and apply to anything Claude produces (chat prose, code comments, ADRs, commit messages, PR descriptions).

## Where the build stands now

Session 0002 (Weeks 3-4, Session 1 of 3) is complete. Four commits added on top of the Phase 1 baseline, CI green on each push:

- `b329c40` Add MigrationRunner and initial event store schema (Phase 2, Weeks 3-4)
- `417778c` Align EventStore.Postgres namespaces with EventSourcingCqrs.Infrastructure convention
- `58f0c70` Add Testcontainers-backed MigrationRunner tests (Phase 2, Weeks 3-4)
- `bde800c` Append performance baseline and v3 cancellation-token deferral to 0002 session log

32 tests passing (22 in Domain.Tests, 10 in Infrastructure.Tests including five new Postgres `MigrationRunner` tests via Testcontainers PostgreSQL 16.6).

What is in place after Session 0002:

- **`migrations/0001_initial_event_store.sql`.** Creates the `event_store` schema and four tables: `events`, `outbox`, `outbox_quarantine`, `schema_migrations`. PostgreSQL 16 syntax exclusive: BIGINT IDENTITY columns, STORED generated columns for `correlation_id` (events and outbox) and `causation_id` (events only), JSONB for payload and metadata. Constraint and index names follow `pk_<table>`, `uq_<table>_<column-or-tuple>`, `ix_<table>_<column-or-purpose>` with the `event_store.` schema prefix carrying the disambiguation context. Partial index `ix_outbox_pending ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` orders pending rows in FIFO sequence for the OutboxProcessor.
- **`EventStore.Postgres` project shell.** `MigrationRunner` with one public method (`RunPendingAsync`), `MigrationRunnerOptions`, `MigrationChecksumMismatchException`. Embedded resources expose `/migrations/*.sql` under the `EventStore.Postgres.Migrations.` resource prefix. `pg_advisory_lock` key `0x4553_5243_515F_4D52L` (ASCII `ESRCQ_MR`) guards the runner across all per-migration transactions. SHA-256 checksums over raw embedded bytes detect post-application edits. Dry-run mode prints the pending list without acquiring the lock or running DDL but still throws on a checksum mismatch.
- **`EventStore.Postgres.Cli` console project.** Single `Program.cs` parsing `migrate` and optional `--dry-run`, reading `EVENT_STORE_CONNECTION_STRING`, invoking the runner. Exit codes 0 success, 1 runner failure, 64 usage, 78 missing env var. The CLI is the operations path; an embedded-startup call from a host's `Program.cs` lands in Session 0004 or later.
- **`.gitattributes`.** `* text=auto eol=lf` with binary carve-outs. SHA-256 checksums on embedded migration files require stable bytes across Windows and Linux/macOS checkouts; making the rule global keeps every text file consistent.
- **Tests.** `PostgresFixture` (IClassFixture, shared `PostgreSqlContainer` at `postgres:16.6-alpine`, per-test database via `CREATE DATABASE test_<guid>`). Five Postgres `MigrationRunner` tests cover first-run application, idempotent re-run, concurrent runners serialized by `pg_advisory_lock` (gate-and-poll-pg_locks pattern), checksum mismatch on a tampered row, and dry-run on an empty database. Constraints asserted against `pg_constraint`, standalone indexes against `pg_indexes`, partial predicate via `pg_indexes.indexdef`.
- **Session log.** `docs/sessions/0002-weeks3-4-postgres-adapter.md` captures the schema decisions, runner decisions, ten Track A flags, the performance baseline for Sessions 0003 and 0004, and the xUnit v3 cancellation-token sweep deferral. `docs/sessions/0002-weeks3-4-postgres-adapter-setup.md` archives this session's planning input under the new two-file convention (`-setup` suffix for planning input, no suffix for session log).

## What is not yet in place

- **PostgreSQL `IEventStore` adapter implementation.** Next work item (Session 0003). The `EventStore.Postgres` project ships the migration runner today; the adapter that implements `IEventStore` against Npgsql is the next addition: `AppendAsync(streamId, expectedVersion, events, ct)` and `ReadStreamAsync(streamId, fromVersion, ct)`, atomic events-plus-outbox write in one `NpgsqlTransaction`, optimistic concurrency mapped to the `uq_events_stream_version` unique constraint, JSON serialization on append and deserialization on read.
- **`OutboxProcessor`.** Session 0004. Drains `event_store.outbox` to the in-process event bus in FIFO `outbox_id` order, with backoff via `next_attempt_at`, error capture in `last_error`, and move-to-quarantine for poison messages. Reminder from Session 0002: `outbox_quarantine.attempt_count` is NOT NULL with no default; the quarantine path must read the live outbox row's `attempt_count` and copy it into the quarantine INSERT.
- **Application layer.** Empty project shell ready. Commands, queries, middleware, `ICommandContext` arrive in Phase 2 with the command pipeline. Real correlation/causation/actor metadata flows from there into `EventStoreRepository`'s envelope construction.
- **KurrentDB and DynamoDB adapters.** Phases 10 and 11 of PLAN.md.
- **Snapshot infrastructure (`ISnapshotStore`, snapshot store implementations).** Phase 12.
- **Projection infrastructure (`IProjectionCheckpoint`, OrderListProjection, etc.).** Weeks 5-6 of the foundation plan.

## What is deferred and why

Items surfaced during prior sessions that will become decisions later. Full discovery context lives in the cited session logs. Condensed for this session:

1. **`ISnapshotStore` and `IProjectionCheckpoint` ports deferred** to Phase 12 and Weeks 5-6 respectively (Session 0001). PLAN.md Phase 1 lists both as Phase 1 deliverables; the reconciled six-week plan delays them until there is a concrete consumer. PLAN.md reconciliation owed in Phase 14.
2. **`EventMetadata.ForCommand` factory deferred** to Phase 2 with the Application command pipeline (Session 0001). The factory needs `ICommand` and `ICommandContext`, both Phase 2 types.
3. **Ch 16 OrderPlaced example has wrong arity** (three args, missing Money Total). Ch 9 four-arg version is authoritative. Track A update owed.
4. **In-memory event store is teaching scaffolding plus dual-purpose Application.Tests fixture, not a fourth peer.** PLAN.md Phase 14 reconciliation owed.
5. **`EventStoreRepository` fills metadata with `Guid.Empty` placeholders and `"Domain"` source.** Real metadata flows in Phase 2 when `ICommandContext` arrives. The PostgreSQL adapter in Session 0003 should accept whatever metadata the repository hands it; the placeholders go away one layer up, not in the adapter.
6. **Ch 16 `AggregateTest<T>.Then` helper is missing `.RespectingRuntimeTypes()`.** Track A: add the call to the printed helper, or switch the example to a manual per-index loop comparison to remove FA-version coupling.
7. **xUnit v3 cancellation-token convention sweep** (Session 0002). The current convention across `Infrastructure.Tests` is to pass `CancellationToken.None` explicitly to all async calls. When Stryker.NET issue #3117 unblocks the upgrade to xUnit v3 (currently the blocker per ADR 0003), the convention switches to `TestContext.Current.CancellationToken` across the test suite in one mechanical sweep.
8. **GlobalPosition placement in the C# type system** (Session 0002). The schema column `global_position` exists on `event_store.events`. The C# binding decision (whether GlobalPosition lives on `EventEnvelope`, on a separate `StoredEvent` wrapper, or in `EventMetadata`) is deferred to Weeks 5-6 when the projection-facing API gets built and a concrete consumer surfaces. Lean: add GlobalPosition to `EventEnvelope`. The adapter in Session 0003 reads and ignores `global_position` for now; the column is populated by the IDENTITY default at INSERT and not surfaced to the caller.

Flag 5 is the one this session's adapter decisions should keep in mind. The adapter does not generate metadata; it serializes whatever `EventEnvelope.Metadata` contains. The Phase 2 command pipeline replaces the placeholders without any adapter change.

## What I want today

This session's planning target is the PostgreSQL `IEventStore` adapter implementation for Weeks 3-4 Session 0003. Four open questions to think through before drafting instructions for Claude Code:

1. **Serializer choice: System.Text.Json vs Newtonsoft.Json.** STJ is the modern default in .NET 10 and the book's voice points toward built-in over external where the built-in suffices. Newtonsoft has richer polymorphism handling, especially for type-name discriminators on `IDomainEvent`. The adapter's serialize-on-write and deserialize-on-read path needs to handle: closed record types for events, the `EventMetadata` record, and round-trip stability so the SHA-256-like equality of payloads holds after a write-read cycle. My current lean: System.Text.Json with a small `JsonSerializerOptions` configured once at adapter construction, plus a type-name resolver for the polymorphism. Push back if you see a reason to prefer Newtonsoft.

2. **Type-name resolution strategy.** Three options: fully-qualified CLR name (`Namespace.TypeName, Assembly`), short logical name (`OrderPlaced`), or an explicit registry mapping logical names to CLR types. Fully-qualified is brittle across refactors; the manuscript's promise that "events outlive code" argues against it. Short logical with an explicit registry is the production-grade answer, and Chapter 11 sets up the upcasting pipeline that already assumes logical naming. The question is what the registry looks like in v1: a hand-maintained `Dictionary<string, Type>` registered in the adapter's options, an attribute-driven scan, or something more elaborate. Open.

3. **Atomic events-plus-outbox transaction shape.** Mostly settled: a single `NpgsqlTransaction` wraps the events INSERT and the outbox INSERTs, with the outbox rows derived from the same `EventEnvelope` list passed to `AppendAsync`. The open question is where the call sits: inside the adapter's `AppendAsync` (one method, one transaction, the adapter knows about the outbox) or one layer up in an `Outbox`-aware decorator around `IEventStore`. The book's commitment is the atomic write; the architectural question is whether the adapter or a decorator owns it. My current lean: inside the adapter. The atomicity is the whole point and a decorator would have to reach into the adapter's transaction handling to preserve it, which leaks worse than the adapter knowing about the outbox table.

4. **Unique-violation-to-ConcurrencyException mapping.** PostgreSQL raises `SQLSTATE 23505` on the `(stream_id, stream_version)` UNIQUE constraint when a concurrent appender lands at the same version. The adapter catches `PostgresException` where `SqlState == "23505"` and `ConstraintName == "uq_events_stream_version"` and throws `ConcurrencyException` with `StreamId`, `ExpectedVersion`, and `ActualVersion` populated. `ActualVersion` requires a follow-up read of `MAX(stream_version)` on the stream because the violation alone does not surface the current head. The question is whether the follow-up read happens inside the catch block (eager, one round-trip cost per concurrency exception, exception always carries the current head) or in a property getter on the exception (lazy, no cost on the throw path, exception holders pay the read when they ask). My current lean: eager in the catch. Concurrency exceptions are not so common that the one extra round-trip matters at the call site, and the eager shape keeps the exception self-contained and reproducible.

After we agree on the four positions, draft the instruction I will send to Claude Code. Format it the way the prior Session 0002 instruction was formatted: clear scope, reference to Chapter 8, ask Claude Code to propose the adapter shape before writing any file.

Once the adapter lands, the pattern repeats for the `OutboxProcessor` in Session 0004.

## Decisions and constraints already made

These are settled. Do not revisit in this session unless something fundamental breaks.

From the pre-Phase 1 baseline:

- .NET 10 LTS, C# 14, pinned via `global.json` with `rollForward: latestFeature`.
- xUnit v2 across all test projects (Stryker.NET issue #3117 blocks v3 for mutation testing).
- FluentAssertions v7 across all test projects (v8 has Xceed commercial license).
- AwesomeAssertions, Shouldly, Verify, plain xUnit Assert noted as alternatives if FluentAssertions migration trigger fires.
- Hand-rolled PostgreSQL event store with raw SQL via Npgsql. No ORM for the event store.
- KurrentDB and DynamoDB as peer adapters in Phases 10 and 11. Marten not implemented as a peer; only mentioned in the manuscript as an alternative readers could swap in.
- PostgreSQL for read models, with a mix of relational tables and JSONB. No Redis or Elasticsearch in v1.
- In-process event bus driven by the outbox. No RabbitMQ or Kafka.
- Single-tenant beyond a tenant ID column. Multi-tenancy patterns discussed in Chapter 13 but not exercised in v1.
- Hexagonal architecture with the layout in `CLAUDE.md`.
- Blazor Server with Tailwind for styling. ASP.NET Core minimal APIs for JSON.
- xUnit v2, FluentAssertions v7, FsCheck, Stryker.NET, Testcontainers, LocalStack as the test stack.
- **Client-name confidentiality.** Never reference consulting clients, employers, or engagements by name in any repository artifact or chat output. Generalize to "a real-world adopter" or similar. Applies across Tracks A/B/C and retroactively. Software products (PostgreSQL, SQL Server, KurrentDB, DynamoDB, Marten) and libraries (Npgsql, Microsoft.Data.SqlClient) are not client names.

Locked in during Session 0001 (Weeks 1-2):

- **`IEventStoreRepository<TAggregate>`, not `IRepository<T>`.** Chapter 8's repository shape.
- **`AggregateRoot` as abstract base class in Domain.Abstractions, not `IAggregateRoot` interface.** The repository constrains on `where TAggregate : AggregateRoot, new()`, so the base class must live alongside the interface in Domain.Abstractions.
- **`IEventStore.ReadStreamAsync` has `fromVersion = 0` default.** Supports snapshot-aware loading in Phase 12 without adding a separate method to the interface.
- **`ConcurrencyException` carries `StreamId`, `ExpectedVersion`, `ActualVersion`.** Caller builds precise diagnostics from the exception alone.
- **`EventEnvelope` is the C# boundary type, not a wire format.** Payload typed as `IDomainEvent`, Metadata typed as `EventMetadata`. The `IEventStore` adapter handles serialization on append and deserialization on read. The in-memory store skips serialization entirely. The PostgreSQL adapter in Session 0003 carries the serialize-on-write, deserialize-on-read responsibility.
- **In-memory event store is teaching scaffolding plus an Application.Tests fixture.** Not a fourth peer. PLAN.md commits to three peers.
- **Six-week reconciled foundation plan governs through mid-June.** PLAN.md's 14-phase pacing is the long-arc reference; the six-week plan is in force for the current work window.
- **`Money` has a currency-less `Zero` identity.** `Money.Zero + somethingInUSD == somethingInUSD`. Empty-order `Total` can compute without forcing a default currency on the Order aggregate.
- **`DomainException` for business-rule violations.** Sealed, single message constructor, lives in Domain.Abstractions alongside `ConcurrencyException`.
- **Test naming convention: snake_case sentence form** (e.g., `AddLine_throws_when_status_is_Placed`). Matches CLAUDE.md "test method names read as sentences with underscores" and the existing tests.
- **`AggregateTest<T>.Then` uses FluentAssertions' `BeEquivalentTo` with `WithStrictOrdering().RespectingRuntimeTypes()`.** The runtime-types option is required because the marker `IDomainEvent` has no members at the declared-type level. Track A flag captured.

Locked in during Session 0002 (Weeks 3-4, Session 1 of 3):

- **Schema with `global_position BIGINT GENERATED ALWAYS AS IDENTITY` as PK on `event_store.events`.** Plus `(stream_id, stream_version)` UNIQUE for optimistic concurrency, `event_id` UNIQUE for idempotency. `stream_id UUID`, `stream_version INT`, `event_id UUID`, `event_type TEXT`, `event_version SMALLINT`, `payload JSONB`, `metadata JSONB`, `occurred_utc TIMESTAMPTZ`. Single non-constraint index `ix_events_correlation_id` on the generated correlation column.
- **STORED generated columns for `correlation_id` and `causation_id` on events; `correlation_id` only on outbox.** Causation tracing is a stream-level concern, not a dispatch-level concern. Cast `(metadata->>'key')::uuid` fails loudly on malformed metadata; that is the contract.
- **Outbox shape.** Separate `event_store.outbox` and `event_store.outbox_quarantine` tables. Partial index `ix_outbox_pending ON event_store.outbox (outbox_id) WHERE sent_utc IS NULL` orders pending rows in FIFO sequence. Outbox has `event_type`, `last_error`, `next_attempt_at`, and a nullable `destination` column for future routing. No FK from quarantine to outbox; quarantine `outbox_id` is a historical reference only.
- **Migration mechanics.** Hand-rolled forward-only SQL files in `/migrations/*.sql`, applied by an Npgsql-based `MigrationRunner` in `EventStore.Postgres`. `pg_advisory_lock`-guarded across the whole batch (key `0x4553_5243_515F_4D52L`), transactional per migration, SHA-256 checksum over raw embedded bytes verified on each run. Dry-run mode prints the pending list without lock or DDL but still throws on checksum mismatch. Embedded resources via `LogicalName="EventStore.Postgres.Migrations.%(FileName)%(Extension)"`. CLI via `EventStore.Postgres.Cli` console project, `EVENT_STORE_CONNECTION_STRING`, exit codes 0/1/64/78.
- **GlobalPosition will eventually live on `EventEnvelope`** (lean from Session 0002 planning). Finalization deferred to Weeks 5-6 when the projection-facing API exposes a concrete consumer.
- **Code-first vs manuscript routing rule.** When the implementation and manuscript disagree, the implementation choice wins and a Track A flag is logged in the session log. Three cases require itemizing the impact rather than only logging the flag:
  - The chapter is the pedagogical anchor for the pattern.
  - The change is a public-API commitment readers can spot from outside the code.
  - The divergence ripples across multiple chapters.
  Otherwise the flag entry in the session log is the full record.
- **Ten Track A flags from Session 0002 are pending batched routing to Track A**, deferred until the end of Weeks 3-4. Captured with full discovery context in `docs/sessions/0002-weeks3-4-postgres-adapter.md`. None of the ten meet a carve-out condition; the batched route is the right shape.

## My working pattern with Claude Code

I review every proposed command and file before approval. I do not use auto-approve options. I want the friction. The previous sessions have been clean because of this pattern, including catching real bugs in multi-step commands and surfacing manuscript discrepancies before they propagate into the implementation.

When I start a new Claude Code session, the first message has the same shape:

> Please re-read CLAUDE.md, docs/PLAN.md, and docs/rules.txt. Then summarize back to me: (1) the work items already complete based on what is in the repo, and (2) the work items still remaining for the current phase. After I confirm the summary is right, we will start on the next item.

I will follow that pattern again when I start the next Claude Code session for the PostgreSQL adapter implementation.

## Writing rules to apply throughout this conversation

These come from `docs/rules.txt` in the repo:

- No em-dashes. Use commas, parentheses, or full stops.
- No "not just X, it's Y" parallel constructions.
- Drop rule-of-three groupings where two or four items would fit better.
- Cut filler vocabulary: delve, elevate, captivating, genuinely, vivid, comprehensive-as-filler.
- No empty corporate-polite framing.
- No restating points already made.
- No strained analogies reaching for meaning.
- Direct, opinionated, plain prose. Specifics over generic claims.
- First-person phrasing where natural.

## How I want this session to flow

We discuss the four adapter questions above. You give me your read on each, including any trade-offs I have not surfaced. I push back where I disagree. We arrive at a position I am confident in.

Once we agree, you draft the instruction I will send to Claude Code. The instruction should set the scope clearly, reference the relevant manuscript chapter (Chapter 8), and ask Claude Code to propose the adapter shape before writing any file.

After the adapter is done, we repeat the pattern for the `OutboxProcessor` in Session 0004. By the end of Weeks 3-4, two more `docs/sessions/` files will exist documenting the path through the rest of the PostgreSQL work, plus their `-setup.md` companions under the new two-file convention.

Let's start with question 1: serializer choice. System.Text.Json vs Newtonsoft.Json for the EventStore.Postgres adapter's serialize-on-write, deserialize-on-read path. What does Chapter 8 mandate and what is open. Surface trade-offs I have not. My current lean is System.Text.Json.
