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

Save this version as `docs/sessions/0002-weeks3-4-postgres-adapter.md` after the next session begins, and create a new `session_setup.md` for the session after that.

---

# Setup for the next session

I am continuing work on a project we have been collaborating on. Here is the full context.

## The project

I am writing a book titled *Event Sourcing & CQRS: A Comprehensive and Practical Guide to Deeper Insights in Your Software Solutions*. The manuscript is complete at 439 pages, 18 chapters, 72 custom diagrams, plus a Part 4 reference implementation specification and a Part 5 resources appendix.

I am preparing to submit a proposal to Pearson/Addison-Wesley for the Vaughn Vernon Signature Series. The proposal package is complete in draft form: table of contents, competitive coverage grid, positioning statement, chapter-by-chapter summaries, two sample chapters (Chapter 11 Event Versioning and Chapter 13 CQRS), author bio, marketing plan, supplementary materials description. The submission is held until the reference implementation is done. Path 1: book and code match end to end before the proposal goes to Pearson.

## About me

Thomas Jaeger. Principal Architect with four decades in software since 1991. I run Legacy to Modern LLC, a one-year-old consultancy focused on modernizing legacy systems into event-sourced cloud-native architectures. The consultancy has its first paying customer; I work it part-time alongside a day job and want to take it full-time once the book gives the practice enough credibility to drive consistent inbound leads.

Earlier in my career I was a Senior Cloud Application Architect at AWS where I introduced DDD, CQRS, and Event Sourcing to engineering teams at Apple, Capital One, Intuit, Verizon, Adobe, Expedia/VRBO, and the Broadridge-UBS Wealth Management Platform. I led Verizon's first executive-level Event Storming workshops and have facilitated dozens of similar sessions since.

I work in C# and .NET. I produce the YouTube channel *Creating Great Software* (1,200 subscribers, top video on Hexagonal Architecture at 25,000 views), write at thomasjaeger.wordpress.com, and share code at github.com/ThomasJaeger.

I attended a presentation by Greg Young while at AWS. That is the extent of my contact with the Event Sourcing community's headline figures so far.

## The reference implementation

Public repo at github.com/ThomasJaeger/event-sourcing-cqrs. MIT licensed. The Phase 1 foundation work item is complete: seven commits added in the previous Claude Code session, CI green on each push.

Built on .NET 10 LTS, C# 14. The book commits to three event-store implementations as first-class peers behind a common abstraction:

- Hand-rolled PostgreSQL (the relational path)
- KurrentDB via gRPC client (the specialized path)
- DynamoDB via LocalStack (the managed-cloud path)

Switching between them is a configuration change, not a code change.

Five aggregates across four bounded contexts. Sales: Order. Fulfillment: Inventory, Shipment. Billing: Payment. Customer Support: read-only context. The Order aggregate is built and tested as of the previous session. The other three ship in Phase 4.

Two process managers, both event-sourced themselves. OrderFulfillmentProcessManager covers the four-branch saga from Chapter 10 with all compensation paths. ReturnProcessManager is the smaller second example. Both ship in Phase 5.

Four projections. OrderListProjection, OrderDetailProjection, CustomerSummaryProjection, InventoryDashboardProjection. Read models live in PostgreSQL with a mix of relational tables and JSONB columns. Ship in Weeks 5-6 of the foundation plan.

Blazor Server UI plus ASP.NET Core minimal API exposing the same operations as JSON. Tailwind for styling. SignalR for live dashboards.

Hexagonal architecture. Domain at the center with no I/O dependencies. Application depends on Domain and Domain.Abstractions only. Infrastructure projects implement the abstractions Domain.Abstractions declares. Hosts (Web, Api, Workers, AdminConsole) depend on Application.

Test stack: xUnit v2, FluentAssertions v7, FsCheck for property-based tests, Stryker.NET for mutation testing on Domain, Testcontainers for PostgreSQL and KurrentDB integration, LocalStack for DynamoDB integration.

## The build plan

`docs/PLAN.md` in the repo describes the 14-phase pacing across 28 weeks. That document remains the long-arc plan and the eventual book reconciliation reference.

The reconciled six-week foundation plan (master setup, May 8, 2026) governs the current work window from May through mid-June and reorders early work to land a runnable Order workflow as quickly as possible:

- Weeks 1-2: in-memory foundation (shipped in the previous session)
- Weeks 3-4: PostgreSQL adapter and outbox (this session's planning target)
- Weeks 5-6: first projection and the read side

PLAN.md's phase numbering does not match the six-week plan's week numbering. Where the two disagree, the six-week plan is in effect through mid-June. PLAN.md reconciliation happens in Phase 14 or sooner if the divergence grows.

The repo-wide rules for Claude Code live in `CLAUDE.md`. My writing style rules live in `docs/rules.txt` and apply to anything Claude produces (chat prose, code comments, ADRs, commit messages, PR descriptions).

## Where the build stands now

Phase 1, Weeks 1-2 is complete. Seven commits added in the previous Claude Code session:

- `923d315` Add Domain and Application projects
- `02ca212` Add CI workflow
- `a6f585f` Add Domain.Abstractions ports and types
- `c8d9e60` Add SharedKernel, in-memory event store, and test scaffolding
- `342dbcb` Add Sales bounded context with Order aggregate and tests
- `10d479e` Add Infrastructure.Tests with event-store and repository coverage
- `42d327e` Add event-storming mapping and Phase 1 session log

CI green on every push. 27 tests passing (22 in Domain.Tests, 5 in Infrastructure.Tests).

What is in place:

- **Domain.Abstractions (9 types).** `IDomainEvent` (marker), `ConcurrencyException` (StreamId, ExpectedVersion, ActualVersion), `DomainException`, `StreamNames` (ForAggregate generic and string-keyed, CategoryFor, ForPartition, SummaryFor, manuscript-verbatim templates), `EventMetadata` (EventId, CorrelationId, CausationId, ActorId, Source, SchemaVersion, OccurredUtc; ForCausedEvent instance method; ForCommand factory deferred), `EventEnvelope` (positional record in SQL-schema column order: StreamId, StreamVersion, EventId, EventType, EventVersion, Payload, Metadata, OccurredUtc), `AggregateRoot` (abstract base, Id protected-set, Version private-set, Raise / Apply / ApplyHistoric / DequeueUncommittedEvents), `IEventStore` (AppendAsync, ReadStreamAsync with `fromVersion = 0` default), `IEventStoreRepository<TAggregate>` (`where TAggregate : AggregateRoot, new()`, LoadAsync, SaveAsync).
- **Domain.** SharedKernel value objects: `Money` with currency-less `Zero` identity and + / - operators, `Address` as a four-field record. Sales bounded context with the `Order` aggregate, seven Order events (`OrderDrafted`, `OrderLineAdded`, `OrderLineRemoved`, `ShippingAddressSet`, `OrderPlaced`, `OrderCancelled`, `OrderShipped`), `OrderLine` entity, `OrderStatus` enum. Order has seven command methods (Draft, AddLine, RemoveLine, SetShippingAddress, Place, Cancel, Ship) with full invariant coverage including manuscript-derived guards plus three gap-fills (AddLine throws on duplicate LineId, SetShippingAddress requires Draft, Ship requires Placed).
- **Infrastructure/EventStore.InMemory.** `InMemoryEventStore` using `Dictionary<Guid, List<EventEnvelope>>`; AppendAsync throws `ConcurrencyException` on stale expectedVersion; ReadStreamAsync with fromVersion slicing. `EventStoreRepository<TAggregate>` is store-agnostic but lives next to the in-memory store for now; it builds envelopes with placeholder metadata until the Application command pipeline arrives in Phase 2.
- **Tests.** Domain.Tests with TestKit (`AggregateTest<TAggregate>` manuscript-verbatim except for one added `.RespectingRuntimeTypes()` call in `Then`; `ThenThrowsAssertion` with `Which` property). OrderTests with 22 cases covering Order's full lifecycle and invariants. Infrastructure.Tests with 3 store tests (round-trip, stale-version `ConcurrencyException`, fromVersion tail) and 2 repository tests (round-trip, no-op on empty).
- **Docs.** `docs/event-storming-mapping.md` captures the sticky-note legend for the Order aggregate (Events orange, Commands blue, Aggregate lilac, Actors yellow). Format ready for the four remaining aggregates and two process managers to append. `docs/sessions/0001-phase1-foundation.md` captures six cross-track flags with discovery context.
- **CI.** `.github/workflows/ci.yml` runs on push and pull_request. .NET 10 SDK, restore + build + test, concurrency-controlled (cancel-in-progress on the same ref), permissions tightened to `contents: read`.

## What is not yet in place

- **PostgreSQL adapter.** Next work item.
- **`migrations/0001_initial_event_store.sql`.** Next work item. Discussed in the prior planning session but not produced.
- **Outbox table and OutboxProcessor.** Part of the next work item.
- **Integration tests with Testcontainers.** Part of the next work item.
- **Application layer.** Empty project shell ready. Commands, queries, middleware, ICommandContext arrive in Phase 2.
- **KurrentDB and DynamoDB adapters.** Phases 10 and 11 of PLAN.md.
- **Snapshot infrastructure (ISnapshotStore, snapshot store implementations).** Phase 12.
- **Projection infrastructure (IProjectionCheckpoint, OrderListProjection, etc.).** Weeks 5-6 of the foundation plan.

## What is deferred and why

Six items surfaced during the previous session that will become decisions later. They are captured in detail in `docs/sessions/0001-phase1-foundation.md`. Condensed for this session:

1. **ISnapshotStore and IProjectionCheckpoint ports deferred** to Phase 12 and Weeks 5-6 respectively. PLAN.md Phase 1 lists both as Phase 1 deliverables; the reconciled six-week plan delays them until there is a concrete consumer. PLAN.md reconciliation owed in Phase 14.
2. **EventMetadata.ForCommand factory deferred** to Phase 2 with the Application command pipeline. The factory needs ICommand and ICommandContext, both Phase 2 types.
3. **Ch 16 OrderPlaced example has wrong arity** (three args, missing Money Total). Ch 9 four-arg version is authoritative. Track A update owed.
4. **In-memory event store is teaching scaffolding plus dual-purpose Application.Tests fixture, not a fourth peer.** PLAN.md Phase 14 reconciliation owed.
5. **EventStoreRepository fills metadata with `Guid.Empty` placeholders and `"Domain"` source.** Real metadata flows in Phase 2 when ICommandContext arrives. PostgreSQL schema decisions in this session should anticipate the eventual metadata richness.
6. **Ch 16 AggregateTest<T>.Then helper is missing `.RespectingRuntimeTypes()`.** Track A: add the call to the printed helper, or switch the example to a manual per-index loop comparison to remove FA-version coupling.

Flag 5 is the one this session's PostgreSQL schema decisions should keep in mind. The metadata columns in `migrations/0001_initial_event_store.sql` need to accommodate real correlation/causation/actor values that Phase 2 will populate, even though Weeks 3-4 ships only placeholders.

## What I want today

This session's planning target is the PostgreSQL adapter and outbox for Weeks 3-4. Specifically, four questions I want to think through with you before sending instructions to Claude Code:

1. **Schema design for the events table.** Chapter 8 is the source of truth, but I want to verify the SQL the manuscript shows against PostgreSQL 16 capabilities and any improvements we should adopt now. What columns, what types, what indexes, what constraints. The unique constraint on `(StreamId, Version)` is non-negotiable; the rest is open. The column order should match `EventEnvelope`'s field order so the adapter mapping reads directly.

2. **Outbox table shape.** Same database per the book. Columns, dispatch model, how the OutboxProcessor in this work item consumes it, how subscriber failures are handled without losing events. The book's commitment is atomic write of events plus outbox in one transaction.

3. **Migration tool decision.** Three real options: FluentMigrator, DbUp, or hand-rolled SQL files run by Npgsql. EF Core migrations is a fourth option but conflicts with the book's "no ORM for the event store" rule, though the migration runner is a separate concern from the event store itself. My current lean is hand-rolled, because it stays closest to the book's voice and reads as a direct demonstration. Push back if you see a reason to prefer one of the others.

4. **Schema migration strategy.** PG 16 syntax exclusive (`gen_random_uuid()`, JSONB, generated columns where useful) or portable SQL. Forward-only or reversible. My current lean: PG 16 exclusive, forward-only. The book commits to PG 16; portability across older versions is not a goal.

After we agree on the four positions, draft the instruction I will send to Claude Code. Format it the way the prior PostgreSQL instruction would have been formatted: clear scope, reference to Chapter 8, ask Claude Code to propose the schema before writing any file.

Once the migration lands, the pattern repeats for the PostgreSQL adapter implementation (`EventStore.Postgres` project, IEventStore implementation against Npgsql, Testcontainers integration tests) and the OutboxProcessor.

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

Locked in during the previous Claude Code session (Weeks 1-2):

- **`IEventStoreRepository<TAggregate>`, not `IRepository<T>`.** Chapter 8's repository shape.
- **`AggregateRoot` as abstract base class in Domain.Abstractions, not `IAggregateRoot` interface.** The repository constrains on `where TAggregate : AggregateRoot, new()`, so the base class must live alongside the interface in Domain.Abstractions.
- **`IEventStore.ReadStreamAsync` has `fromVersion = 0` default.** Supports snapshot-aware loading in Phase 12 without adding a separate method to the interface.
- **`ConcurrencyException` carries `StreamId`, `ExpectedVersion`, `ActualVersion`.** Caller builds precise diagnostics from the exception alone.
- **`EventEnvelope` is the C# boundary type, not a wire format.** Payload typed as `IDomainEvent`, Metadata typed as `EventMetadata`. The IEventStore adapter handles serialization on append and deserialization on read. The in-memory store skips serialization entirely. The PostgreSQL adapter in the next work item carries the serialize-on-write, deserialize-on-read responsibility.
- **In-memory event store is teaching scaffolding plus an Application.Tests fixture.** Not a fourth peer. PLAN.md commits to three peers.
- **Six-week reconciled foundation plan governs through mid-June.** PLAN.md's 14-phase pacing is the long-arc reference; the six-week plan is in force for the current work window.
- **`Money` has a currency-less `Zero` identity.** `Money.Zero + somethingInUSD == somethingInUSD`. Empty-order `Total` can compute without forcing a default currency on the Order aggregate.
- **`DomainException` for business-rule violations.** Sealed, single message constructor, lives in Domain.Abstractions alongside `ConcurrencyException`.
- **Test naming convention: snake_case sentence form** (e.g., `AddLine_throws_when_status_is_Placed`). Matches CLAUDE.md "test method names read as sentences with underscores" and the 22 existing tests.
- **`AggregateTest<T>.Then` uses FluentAssertions' `BeEquivalentTo` with `WithStrictOrdering().RespectingRuntimeTypes()`.** The runtime-types option is required because the marker `IDomainEvent` has no members at the declared-type level. Track A flag captured.

## My working pattern with Claude Code

I review every proposed command and file before approval. I do not use auto-approve options. I want the friction. Phase 1 has been clean because of this pattern, including catching real bugs in multi-step commands and surfacing manuscript discrepancies before they propagate into the implementation.

When I start a new Claude Code session, the first message has the same shape:

> Please re-read CLAUDE.md, docs/PLAN.md, and docs/rules.txt. Then summarize back to me: (1) the work items already complete based on what is in the repo, and (2) the work items still remaining for the current phase. After I confirm the summary is right, we will start on the next item.

I will follow that pattern again when I start the next Claude Code session for the PostgreSQL migration work.

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

We discuss the four PostgreSQL/outbox questions above. You give me your read on each, including any trade-offs I have not surfaced. I push back where I disagree. We arrive at a position I am confident in.

Once we agree, you draft the instruction I will send to Claude Code. The instruction should set the scope clearly, reference the relevant manuscript chapter (Chapter 8), and ask Claude Code to propose the schema before writing any file.

After the migration is done, we repeat the pattern for the PostgreSQL adapter implementation, then for the OutboxProcessor. Each gets its own session. By the end of Weeks 3-4, three more `docs/sessions/` files will exist, documenting the path through the PostgreSQL work.

Let's start with question 1: the schema design for the events table. What columns, what types, what indexes, what constraints. What does Chapter 8 mandate and what is open to choice. Verify the SQL the manuscript shows against PostgreSQL 16 capabilities and any improvements we should adopt now. Keep an eye on the EventEnvelope column order so the adapter mapping is direct.
