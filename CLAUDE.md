# CLAUDE.md

This file instructs Claude Code on how to work in this repository. Read it before generating any code.

## What this repository is

This is the reference implementation for the book *Event Sourcing & CQRS: A Comprehensive and Practical Guide to Deeper Insights in Your Software Solutions* by Thomas Jaeger. The codebase exists to make the book's patterns concrete and runnable. Every chapter that teaches a pattern has corresponding code here that demonstrates it.

This is a teaching artifact, not a production framework. Readers clone it, run it, study it, and use it as a reference for their own systems. They do not deploy it.

## What "good" looks like in this repository

Code in this repo must reflect the patterns the book teaches. The book is opinionated. The code must be opinionated in the same direction. When there is a choice between idiomatic .NET and the book's pattern, the book's pattern wins. A reader who copies a snippet from the book and a snippet from the repo should see the same shape.

The code should be readable as a study text, not just runnable. Long methods are worse than smaller ones with clear names. Generic abstractions that hide the pattern are worse than concrete code that shows it. Comments that explain the why are valuable. Comments that restate the what are noise.

## Stack and constraints

* .NET 10 LTS, C# 14. Pinned to .NET 10 via `global.json` at the repo root.
* Four event-store implementations as first-class peers behind a common abstraction:
  - PostgreSQL 16 (hand-rolled, the relational path)
  - SQL Server 2022 (hand-rolled, the relational path on the Microsoft stack)
  - KurrentDB (the specialized path)
  - DynamoDB (the managed-cloud path, via LocalStack for local dev and tests)
* PostgreSQL 16 for read models, with a mix of relational tables and JSONB columns.
* Blazor Server for the UI, ASP.NET Core minimal APIs for the JSON API.
* Tailwind for styling.
* SignalR for live dashboard updates.
* xUnit, FluentAssertions, FsCheck (property-based tests), Stryker.NET (mutation tests), Testcontainers (PostgreSQL, SQL Server, and KurrentDB), LocalStack (DynamoDB).
* Docker Compose to run the whole system locally with one command.

## Architectural rules

These are non-negotiable. If a generated solution conflicts with one of these, the rule wins.

### Hexagonal architecture (ports and adapters)

* Domain at the center. No I/O dependencies in Domain.
* Application depends on Domain and Domain.Abstractions only.
* Infrastructure projects implement the abstractions Domain.Abstractions declares.
* Hosts (Web, Api, Workers, AdminConsole) depend on Application.
* Domain.Tests has no infrastructure dependencies and runs in microseconds.

### Events

* Events are immutable records. Use C# `record` types with init-only properties.
* Events live in their own folder per bounded context: `Domain/Sales/Events`, `Domain/Fulfillment/Events`, etc.
* Every event has metadata kept in an `EventEnvelope`: EventId, AggregateId, Version, OccurredAt, CorrelationId, CausationId, Actor. The metadata is a separate type from the event payload, as the manuscript Chapter 8 specifies.
* Events are serialized as JSON. Schema is enforced at the type level.
* Never mutate an event. Never delete an event. Never rewrite history. If something needs correcting, append a compensating event.

### Aggregates

* Aggregates rebuild their state from events. They never load state from a snapshot or a read model directly. Snapshots are loaded by the repository, which then applies subsequent events.
* Aggregates expose command methods that produce new events. They do not return data.
* Aggregates enforce invariants in command methods. Violations throw domain-specific exceptions.
* Aggregates reconstruct via an `Apply(IDomainEvent)` method. This method is the only way state changes inside the aggregate.
* No public setters on aggregate state. Properties are private set or init-only.
* Aggregate boundaries are tight. If a command needs two aggregates, use a process manager instead.
* Five aggregates ship in v1: Order, Inventory, Shipment, Payment, plus Customer reference data, across four bounded contexts (Sales, Fulfillment, Billing, Customer Support).

### Command handlers

* Command handlers load the aggregate, call the command method, persist the resulting events.
* Persistence goes through `IEventStore`, never directly to a database.
* Optimistic concurrency is enforced on the expected version. Conflicts throw `ConcurrencyException`.
* Command handlers do not call other command handlers. Cross-aggregate work happens via process managers.
* Cross-cutting middleware: logging (with correlation IDs), validation, idempotency-key enforcement.

### Event store abstraction

* `IEventStore` defined in Domain.Abstractions. Four implementations: `EventStore.Postgres`, `EventStore.SqlServer`, `EventStore.Kurrent`, `EventStore.DynamoDb`.
* Switching between them is a configuration change, not a code change. The abstraction is real, not aspirational.
* PostgreSQL adapter: hand-rolled SQL via Npgsql. Schema in `migrations/`. Append is atomic per stream with unique constraint on (StreamId, Version). Outbox table updated in the same transaction.
* SQL Server adapter: hand-rolled SQL via Microsoft.Data.SqlClient. Schema in `migrations/`. Append is atomic per stream with unique constraint on (StreamId, Version). Outbox table updated in the same transaction.
* KurrentDB adapter: gRPC client. Native catch-up subscriptions used for projections instead of polling.
* DynamoDB adapter: composite key (partition = AggregateType#AggregateId, sort = Version), conditional writes with `attribute_not_exists(Version)`. DynamoDB Streams feeds projections.
* No ORM for the event store. Read models may use Entity Framework Core if it helps; the event store does not.

### Projections

* Pull-based with checkpoints. A projection has a checkpoint, reads events from a position forward, updates the read model.
* Projections are idempotent. Re-reading the same event must produce the same result.
* Projections do not call back into the write side.
* Each projection has its own checkpoint. Projections never share state.
* Four projections in v1: OrderListProjection, OrderDetailProjection, CustomerSummaryProjection, InventoryDashboardProjection. Mix of relational and JSONB read models.
* Trigger mechanism is per event store: polling and LISTEN/NOTIFY for PostgreSQL, polling for SQL Server, native subscriptions for KurrentDB, DynamoDB Streams for DynamoDB.

### Process managers

* Process managers are themselves event-sourced with their own state and stream.
* Two process managers in v1: OrderFulfillmentProcessManager (four compensation branches), ReturnProcessManager (smaller second example).
* Compensating actions are explicit. Every step that can fail has a compensation path that is implemented and tested.
* Timeouts via a delay queue (PostgreSQL table holding scheduled commands).
* Idempotency keys on commands so retries do not produce duplicate effects.
* Process managers do not access aggregates directly. They send commands.

### Tests

* Aggregate tests use Given-When-Then: `Given(events).When(command).Then(expectedEvents)`.
* Projection tests feed a known event stream and assert on the resulting read model.
* Process manager tests feed events and assert on commands emitted plus internal state.
* Property-based tests via FsCheck for invariants and serialization roundtrips.
* Replay tests against historical event streams.
* Integration tests use Testcontainers (PostgreSQL, SQL Server, KurrentDB) and LocalStack (DynamoDB). No mocking of these stores.
* Mutation testing via Stryker.NET on the Domain project.
* Chaos and failure injection tests for projections and the outbox.

## Folder layout

```
/src
  /Domain
    /Sales              // Order aggregate, events, value objects
    /Fulfillment        // Inventory, Shipment aggregates
    /Billing            // Payment aggregate
    /CustomerSupport    // Read-only context
    /SharedKernel       // Money, Address, identifiers
  /Domain.Abstractions  // ports: IEventStore, IRepository, etc.
  /Application
    /Commands
    /Queries
    /Middleware
  /ProcessManagers
    /OrderFulfillment
    /Returns
  /Projections
    /OrderList
    /OrderDetail
    /CustomerSummary
    /InventoryDashboard
    /Infrastructure
  /Infrastructure
    /EventStore.Postgres
    /EventStore.SqlServer
    /EventStore.Kurrent
    /EventStore.DynamoDb
    /ReadModels.Postgres
    /Outbox
    /Snapshots
    /Versioning
  /Hosts
    /Web                // Blazor Server
    /Api                // JSON API
    /Workers            // hosted services
    /AdminConsole       // operational tools
  /Migration            // standalone Chapter 18 example
/tests
  /Domain.Tests
  /Application.Tests
  /ProcessManagers.Tests
  /Projections.Tests
  /Infrastructure.Tests
  /IntegrationTests
  /PropertyTests
/migrations
/docs
/docker
```

The folder structure maps to chapters. Domain shows Chapters 7 and 9. Application shows Chapters 8 and 13. ProcessManagers shows Chapter 10. Projections shows Chapter 13. Infrastructure shows Chapter 8 plus parts of 11, 12, 17. AdminConsole shows Chapter 17. Migration shows Chapter 18.

## Naming conventions

* Commands are imperative: `PlaceOrder`, `AuthorizePayment`, `ProcessReturn`.
* Events are past tense: `OrderPlaced`, `PaymentAuthorized`, `ReturnProcessed`.
* Aggregates are nouns: `Order`, `Inventory`, `Payment`.
* Process managers end in `ProcessManager`: `OrderFulfillmentProcessManager`, `ReturnProcessManager`.
* Projections end in `Projection`: `OrderListProjection`, `InventoryDashboardProjection`.
* Read models end in `ReadModel`: `OrderListReadModel`, `OrderDetailReadModel`.
* Repositories end in `Repository`: `OrderRepository`, `InventoryRepository`.
* Adapters are named for what they adapt: `EventStore.Postgres`, `EventStore.SqlServer`, `EventStore.Kurrent`, `EventStore.DynamoDb`.
* Test classes are named for the type under test, suffixed `Tests`.
* Test method names read as sentences with underscores: `Cancelling_a_shipped_order_throws`.

## What Claude Code should not do

Do not introduce dependencies without asking. Every new NuGet package is a decision that affects the book.

Do not generate generic abstractions ahead of need. The book teaches concrete patterns. A reader who sees `OrderRepository` learns more than one who sees `IRepository<TAggregate, TId, TVersion>`.

Do not add CQRS-shaped CRUD. A command that just sets fields and emits a `FieldsUpdated` event is CRUD with extra steps. Commands should represent business intent.

Do not add features outside the book's scope. The book defines what ships. If a feature is not discussed in any chapter, it does not belong in v1.

Four first-class peers: PostgreSQL hand-rolled, SQL Server hand-rolled, KurrentDB, DynamoDB. Marten remains a discussed alternative for PostgreSQL, not a shipped peer. No other peers without explicit decision.

Do not implement Redis, Elasticsearch, or S3 read models. Chapter 13 discusses these as options. The reference implementation uses PostgreSQL for read models.

Do not introduce distributed messaging (RabbitMQ, Kafka). The reference implementation uses an in-process event bus driven by the outbox.

Do not optimize prematurely. The book has a snapshots chapter for performance. Until that chapter's patterns are introduced (Phase 12), write straight code.

Do not write defensive code that hides bugs. Bad input should produce clear errors, not silent fallbacks.

Do not assume async is always correct. For genuinely synchronous operations, use synchronous methods.

Do not generate placeholder TODOs and pretend the code is complete. If something is incomplete, say so explicitly.

Do not pull patterns from later phases into the current phase. The phase boundaries in PLAN.md exist to prevent the implementation from sprawling.

Do not target a .NET version other than 10. The repo is pinned via `global.json`. If the build fails because the .NET 10 SDK is not installed, surface the error rather than falling back to a different SDK.

## What Claude Code should do

Refer to the book's chapter explicitly when generating code. A comment like `// Pattern from Chapter 11: Upcasting` makes the mapping clear.

Write tests alongside code, not after. Untested code in this repo is a defect.

Keep methods short. If a command method exceeds 30 lines, find the abstraction.

Match the book's voice in code comments. Direct, opinionated, plain. No corporate hedging.

Use C# 14 features where they make the code clearer (extension members, partial constructors, field-backed properties), but do not reach for features just to use them.

When in doubt, generate the simplest version that demonstrates the pattern, and ask whether to elaborate.

Verify the abstraction holds. When working on Phase 2's SQL Server adapter and Phases 10-11's KurrentDB and DynamoDB adapters, if `IEventStore` does not fit cleanly, surface it. The SQL Server adapter is the first real stress test of the abstraction because it forces a second engine before the more-different KurrentDB and DynamoDB adapters arrive. Better to fix the abstraction than to leak adapter-specific concepts upward.

## Reading order for new context

When starting a session, the relevant context lives in:

1. `CLAUDE_CODE_PREAMBLE.md` for the working pattern Claude Code should follow in every session (propose before writing, stop and ask before deviating, log cross-track flags, commit per logical unit).
2. `docs/rules.txt` for the writing style this repository expects from anything you produce (chat prose, code comments, ADRs, commit messages, PR descriptions).
3. This file (CLAUDE.md) for repo-wide rules.
4. `docs/PLAN.md` for the current phase's scope and out-of-scope items.
5. `docs/ARCHITECTURE.md` (when written) for cross-cutting decisions.
6. The relevant book chapter or chapters for the current phase, which the human will provide in the session.

Always check the plan before starting work. The plan defines what is in scope for the current phase. The chapter defines the patterns to implement.
