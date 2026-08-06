# CLAUDE.md

This file instructs Claude Code on how to work in this repository. Read it before generating any code.

## What this repository is

This is the reference implementation for the book *Event Sourcing & CQRS: A Comprehensive and Practical Guide to Deeper Insights in Your Software Solutions* by Thomas Jaeger. The codebase exists to make the book's patterns concrete and runnable. Every chapter that teaches a pattern has corresponding code here that demonstrates it.

This is a production-grade reference implementation. Readers clone it, run it, study it, and adapt it for their own systems, including commercial and production services. The repository does not ship as a redistributable framework, and the orchestrator's own services adopt the patterns and code shown here. Every line of code is written as if it were already running in a production service that the reader operates.

## Source-of-truth hierarchy

The reference implementation source code in this repository is the highest-priority source of truth for the book at ~/Documents/GitHub/event-sourcing-cqrs-book/. When the manuscript depicts the current canonical shape of a domain type, API signature, schema column, type token, parameter name, or any other observable artifact and disagrees with the code in this repository, the code is canonical. The book gets normalized to match.

Cluster work in the book repo that touches potentially-divergent surfaces verifies against this repository's code before scoping. Pre-flight reads of the manuscript are starting points; the code is authority.

Deliberate historical-shape pedagogy in the book stays as-written. Ch 11's upcasting V1 to V4 progression depicts an event's evolution across schema versions; the V1 shape is intentionally divergent from the current code. Ch 18's legacy-CRUD depictions are intentional. Pedagogical divergence is exempt; current-state divergence is not.

This rule applies symmetrically across both Claude Code instances and the Claude.ai planner. Same rule appears in the book repo's CLAUDE.md and HANDOFF.md.

## Attribution convention

Commit messages, session log content, and doc-edit prose in this repository carry no Co-Authored-By or other Claude / Anthropic attribution. The AI-assistance pattern is internal to the working model, not a public artifact. Historical commits carrying attribution stay as-is; enforcement is forward-only.

This rule applies symmetrically across both Claude Code instances and the Claude.ai planner. Same rule appears in the book repo's CLAUDE.md and HANDOFF.md.

## What "good" looks like in this repository

Code in this repo must reflect the patterns the book teaches. The book is opinionated. The code must be opinionated in the same direction. A reader who copies a snippet from the book and a snippet from the repo should see the same shape. When the chapter prose depicts a teaching-friendly shortcut and production practice would do it differently, the code ships the production version and the chapter prose updates to match (manuscript reconciliation tracks the divergence as an F-NNNN candidate).

The code should be readable. Long methods are worse than smaller ones with clear names. Methods named for their behavior over methods named for their structure. Concrete code over premature abstraction. Comments that explain the why are valuable; comments that restate the what are noise. These are production virtues, not teaching virtues; they govern this code because production-grade .NET reads this way, not because a textbook would.

## Production quality is non-negotiable

Production-grade correctness, rigor, and operational hygiene govern every line of code in this repository. There is no axis on which a teaching-friendly shortcut wins. When a teaching-friendly version of a piece of code and the production version diverge, the production version ships and the chapter prose updates to depict it. This rule overrides any framing elsewhere in this file or in any ADR that suggests teaching clarity competes with production rigor.

Concretely:

* Configuration validates at startup. Required dependencies throw on missing input with named exception types. No silent defaults. No downstream surprises.
* Failures surface as named exceptions at the boundary that owns them. A defect in the Web host's dispatch surfaces in the Web host's logs, not as a 400 from the Api host. Where a registry, validator, or guard can catch a defect in-process at the originating boundary, it does.
* Cancellation propagates through async chains. No fire-and-forget. No swallowed cancellation tokens. Long-running loops check the token at every iteration boundary.
* Lifecycles are managed. `IAsyncDisposable` is awaited. Scoped services resolve in scopes. Singletons are stateless or guarded with explicit synchronization.
* Logging is structured. Secrets never appear in logs or in exception messages that get logged. TLS is the default for any cross-host transport.
* Error handling is intentional. `catch (Exception)` either rethrows, translates to a named exception type, or surfaces a documented failure mode. None of them swallow.
* Tests assert on real behavior the production caller depends on. Integration tests exercise the wire format. Unit tests exercise the contract, not the incidental implementation.

Two architectural decisions on disk (the self-contained event-store-adapter rule and the process-manager type-hierarchy rule) were originally justified on pedagogical-transparency grounds. Both decisions survive, and both now carry amendments stating their production grounds: ADR 0004 on the four adapters' unrelated failure modes and its measured duplication cost, ADR 0012 on the type-system boundary that keeps process-manager events off the outbox and the buffer split that survives a failed append. Neither rule changed.

## Stack and constraints

* .NET 10 LTS, C# 14. Pinned to .NET 10 via `global.json` at the repo root.
* Four event-store implementations as first-class peers behind a common abstraction:
  - PostgreSQL 16 (hand-rolled, the relational path)
  - SQL Server (hand-rolled, the relational path on the Microsoft stack). 2019 is the version floor and what CI proves, because the schema depends on a UTF-8 collation that arrived in 2019; 2022 is the primary target.
  - KurrentDB (the specialized path)
  - DynamoDB (the managed-cloud path, via LocalStack for local dev and tests)
* PostgreSQL 16 for read models, with a mix of relational tables and JSONB columns.
* Blazor Server for the UI, ASP.NET Core minimal APIs for the JSON API.
* Tailwind for styling.
* In-process notification dispatch for live dashboard updates (server-rendered Blazor circuits, ADR 0032).
* xUnit, FluentAssertions, FsCheck (property-based tests), Testcontainers (PostgreSQL, SQL Server, and KurrentDB), LocalStack pinned to 4.14.0 (DynamoDB). The pin is required, not hygiene: `localstack/localstack:latest` exits 55 on startup with "License activation failed" unless an auth token is set, and the tag list carries a community-archive tag, so the community edition is archived rather than superseded. 4.14.0 is the last tag that starts with no token.
* Docker Compose to run the whole system locally with one command.

## Architectural rules

These are non-negotiable. If a generated solution conflicts with one of these, the rule wins.

### Hexagonal architecture (ports and adapters)

* Domain at the center. No I/O dependencies in Domain.
* Application depends on Domain and Domain.Abstractions only. Context-specific read-side ports (e.g., `IOrderListStore`) live at `Domain/{Context}/ReadModels/`; context-agnostic ports live in Domain.Abstractions. Projections holds adapters and references Domain.
* Infrastructure projects implement the abstractions Domain.Abstractions declares.
* Hosts (Web, Api, Workers, AdminConsole) depend on Application.
* Domain.Tests has no infrastructure dependencies and runs in microseconds.

### Events

* Events are immutable records. Use C# `record` types with init-only properties.
* Events live in their own folder per bounded context: `Domain/Sales/Events`, `Domain/Fulfillment/Events`, etc.
* Every event is stored in an `EventEnvelope`, which declares `StreamId`, `StreamVersion`, `EventId`, `EventType`, `EventVersion`, `Payload`, `Metadata`, `OccurredUtc`, and `GlobalPosition`. The `Metadata` member is its own record, `EventMetadata`, declaring `EventId`, `CorrelationId`, `CausationId`, `ActorId`, `Source`, `OccurredUtc`, and `Tenant`. The metadata is a separate type from the event payload, as the manuscript Chapter 8 specifies.
* Events are serialized as JSON. Schema is enforced at the type level.
* Never mutate an event. Never delete an event. Never rewrite history. If something needs correcting, append a compensating event.

### Aggregates

* Aggregates rebuild their state from events. They never load state from a snapshot or a read model directly. Snapshots are loaded by the repository, which then applies subsequent events.
* Aggregates expose command methods that produce new events. They do not return data.
* Aggregates enforce invariants in command methods. Violations throw domain-specific exceptions.
* Aggregates reconstruct via an `Apply(IDomainEvent)` method. This method is the only way state changes inside the aggregate.
* No public setters on aggregate state. Properties are private set or init-only.
* Aggregate boundaries are tight. If a command needs two aggregates, use a process manager instead.
* Five aggregates ship in v1, each a subclass of `AggregateRoot`: Order (Sales), Inventory and Shipment (Fulfillment), Payment (Billing), and UserRoles (Access). They sit across five bounded contexts: those four plus Customer Support, which owns no aggregate and reads from other contexts' projections.

### Command handlers

* Command handlers load the aggregate, call the command method, persist the resulting events.
* Persistence goes through `IEventStore`, never directly to a database.
* Optimistic concurrency is enforced on the expected version. Conflicts throw `ConcurrencyException`.
* Command handlers do not call other command handlers. Cross-aggregate work happens via process managers.
* Cross-cutting middleware, in the order `AddApplication` registers it: logging (with correlation IDs), authorization, idempotency-key enforcement, validation. Authorization sits inside logging and before idempotency per ADR 0028, so an unauthorized attempt is logged and consumes no idempotency storage.

### Event store abstraction

* `IEventStore` defined in Domain.Abstractions. Four shipped peers, and the four values `EventStoreProvider` offers: `EventStore.Postgres`, `EventStore.SqlServer`, `EventStore.Kurrent`, `EventStore.DynamoDb`. A fifth type implements the interface, `InMemoryEventStore`, which no host composes; it serves tests and the migration demo.
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
* Eight projections register in v1. Four are user-facing read models (OrderListProjection, OrderDetailProjection, CustomerSummaryProjection, InventoryDashboardProjection), a mix of relational and JSONB. Four support the system: SkuToInventoryIdProjection and OrderIdToPaymentIdProjection (projection-private cross-aggregate lookups, ADR 0020), CurrentRolesProjection (the RBAC current-roles read model), and OrderThroughputProjection (the order-throughput meter).
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
* Mutation testing on the Domain project.
* Chaos and failure injection tests for projections and the outbox.

## Folder layout

```
/src
  /Domain
    /Sales                    // Order aggregate, events, value objects
    /Fulfillment              // Inventory, Shipment aggregates
    /Billing                  // Payment aggregate
    /CustomerSupport          // Read-only context
    /Access                   // Event-sourced user-to-role assignments
    /SharedKernel             // Money, Address, identifiers
  /Domain.Abstractions        // ports: IEventStore, IEventStoreRepository, etc.
  /Application
    /Commands
    /Queries
    /Middleware
    /Pipelines                // command and query pipeline behaviors
    /Authentication
    /Authorization
    /Context                  // ambient command, query, and tenant context
    /SignalR                  // IHubBackplaneConnection
  /ProcessManagers
    /OrderFulfillment
    /Returns
  /Projections
    /OrderList
    /OrderDetail
    /CustomerSummary
    /InventoryDashboard
    /CurrentRoles             // RBAC current-roles read model
    /SkuToInventoryId         // projection-private lookup (ADR 0020)
    /OrderIdToPaymentId       // projection-private lookup (ADR 0020)
    /OrderThroughput          // order-throughput meter
    /Infrastructure           // checkpointing, batched catch-up, failure handling
  /Infrastructure
    /EventStore.Postgres
    /EventStore.Postgres.Cli  // migrate entry point
    /EventStore.SqlServer
    /EventStore.Kurrent
    /EventStore.DynamoDb
    /EventStore.InMemory      // test and demo adapter
    /Migrations.Postgres      // engine-agnostic migration runner (ADR 0004)
    /ReadModels.Postgres
    /Outbox
    /SignalR
    /Versioning
  /Hosts
    /Web                      // Blazor Server
    /Api                      // JSON API
    /Workers                  // hosted services
    /AdminConsole             // operational tools
  /Migration                  // standalone Chapter 18 example
/tests
  /Domain.Tests
  /Application.Tests
  /ProcessManagers.Tests
  /Projections.Tests
  /Infrastructure.Tests
  /IntegrationTests
  /PropertyTests
  /Workers.Tests
  /Hosts.Web.Tests
  /Hosts.AdminConsole.Tests
  /Migration.Tests
  /EventStore.ContractTests   // the one suite all four adapters pass
  /TestInfrastructure         // shared fixtures, no facts of its own
/migrations
/docs
/docker
/scripts
```

The folder structure maps to chapters. Domain shows Chapters 7 and 9. Application shows Chapters 8 and 13. ProcessManagers shows Chapter 10. Projections shows Chapter 13. Infrastructure shows Chapter 8 plus parts of 11, 12, 17. AdminConsole shows Chapter 17. Migration shows Chapter 18.

## Naming conventions

* Commands are imperative: `PlaceOrder`, `AuthorizePayment`, `ProcessReturn`.
* Events are past tense: `OrderPlaced`, `PaymentAuthorized`, `ReturnProcessed`.
* Aggregates are nouns: `Order`, `Inventory`, `Payment`.
* Process managers end in `ProcessManager`: `OrderFulfillmentProcessManager`, `ReturnProcessManager`.
* Projections end in `Projection`: `OrderListProjection`, `InventoryDashboardProjection`.
* Read-model rows end in `Row`: `OrderListRow`, `OrderDetailRow`. Their read ports end in `Store`: `IOrderListStore`, `IOrderDetailStore`. The write ports a projection batches through end in `UnitOfWork`: `IOrderListUnitOfWork`. All three live under `Domain/{Context}/ReadModels/`.
* Repositories end in `Repository` and are generic over what they load: `EventStoreRepository<TAggregate>`, `SnapshottingEventStoreRepository<TAggregate, TSnapshot>`, `ProcessManagerRepository<TPm>`. No per-aggregate repository type exists, because loading by replay is identical across aggregates and all four event stores sit behind one port.
* Adapters are named for what they adapt: `EventStore.Postgres`, `EventStore.SqlServer`, `EventStore.Kurrent`, `EventStore.DynamoDb`.
* Test classes are named for the type under test, suffixed `Tests`.
* Test method names read as sentences with underscores: `Cancelling_a_shipped_order_throws`.

## Confidentiality and client references

Never reference consulting clients, employers, or engagements by name anywhere in this repository or in chat output. The rule covers code, comments, ADRs, commit messages, PR descriptions, session logs, and documentation. When the reasoning behind a decision draws on a specific client or engagement, generalize the reference. Phrases like "a planned implementation for a real-world adopter" or "a future enterprise consumer" carry the reasoning without the name.

The rule applies to all three tracks: book content (Track A), code planning (Track B), and code execution (Track C). It applies retroactively. If you notice a client name in any draft or artifact, flag it for correction before commit.

Software product names (PostgreSQL, SQL Server, KurrentDB, DynamoDB, Marten) and library names (Npgsql, Microsoft.Data.SqlClient) are not client names and are fine to use.

## What Claude Code should not do

Do not introduce dependencies without asking. Every new NuGet package is a decision that affects the book.

Do not generate generic abstractions ahead of need. A type parameter earns its place when more than one concrete case already needs it, never when one might later. `EventStoreRepository<TAggregate>` is generic because five aggregates and four event stores already share a single load-and-save path; an `IRepository<TAggregate, TId, TVersion>` with one implementation and no second caller is the shape to refuse.

Do not add CQRS-shaped CRUD. A command that just sets fields and emits a `FieldsUpdated` event is CRUD with extra steps. Commands should represent business intent.

Do not add features outside the book's scope. The book defines what ships. If a feature is not discussed in any chapter, it does not belong in v1.

Four first-class peers: PostgreSQL hand-rolled, SQL Server hand-rolled, KurrentDB, DynamoDB. Marten remains a discussed alternative for PostgreSQL, not a shipped peer. No other peers without explicit decision.

Do not implement Redis, Elasticsearch, or S3 read models. Chapter 13 discusses these as options. The reference implementation uses PostgreSQL for read models.

Do not introduce distributed messaging (RabbitMQ, Kafka). The reference implementation uses an in-process event bus driven by the outbox.

Do not optimize prematurely. The book has a snapshots chapter for performance. Until that chapter's patterns are introduced (Phase 15), write straight code.

Do not write defensive code that hides bugs. Bad input should produce clear errors, not silent fallbacks.

Do not assume async is always correct. For genuinely synchronous operations, use synchronous methods.

Do not generate placeholder TODOs and pretend the code is complete. If something is incomplete, say so explicitly.

Do not pull patterns from later phases into the current phase. The phase boundaries in PLAN.md exist to prevent the implementation from sprawling.

Do not target a .NET version other than 10. The repo is pinned via `global.json`. If the build fails because the .NET 10 SDK is not installed, surface the error rather than falling back to a different SDK.

## What Claude Code should do

Hold every line to production-grade correctness, rigor, and operational hygiene. See "Production quality is non-negotiable" above. When in doubt, write what a production team would write, not what a textbook would show.

Refer to the book's chapter explicitly when generating code. A comment like `// Pattern from Chapter 11: Upcasting` makes the mapping clear.

Write tests alongside code, not after. Untested code in this repo is a defect.

Keep methods short. If a command method exceeds 30 lines, find the abstraction.

Match the book's voice in code comments. Direct, opinionated, plain. No corporate hedging.

Use C# 14 features where they make the code clearer (extension members, partial constructors, field-backed properties), but do not reach for features just to use them.

When in doubt, generate the simplest version that demonstrates the pattern, and ask whether to elaborate.

Verify the abstraction holds. When working on Phase 2's SQL Server adapter and Phases 13-14's KurrentDB and DynamoDB adapters, if `IEventStore` does not fit cleanly, surface it. The SQL Server adapter is the first real stress test of the abstraction because it forces a second engine before the more-different KurrentDB and DynamoDB adapters arrive. Better to fix the abstraction than to leak adapter-specific concepts upward.

## Reading order for new context

When starting a session, the relevant context lives in:

1. `CLAUDE_CODE_PREAMBLE.md` for the working pattern Claude Code should follow in every session (propose before writing, stop and ask before deviating, log cross-track flags, commit per logical unit).
2. `docs/TDD_RULES.md` for the test-first discipline (the RED-before-production-code cycle, the anti-theater enforcement, the scope where TDD is mandatory versus spike-then-stabilize versus judgment). It extends the working pattern; it does not override it.
3. `docs/ai-writing-style-source.txt` for the writing style this repository expects from anything you produce (chat prose, code comments, ADRs, commit messages, PR descriptions). The file is reference source material (a transcript), and it is the only source: no bullet-form restatement of the rules exists in this repository.
4. This file (CLAUDE.md) for repo-wide rules.
5. `docs/PLAN.md` for the current phase's scope and out-of-scope items.
6. `docs/ARCHITECTURE.md` for the cross-cutting decisions: what they are, how they compose, and which ADR owns each one. It routes rather than restates, so the ADR it points at is always the authority.
7. The relevant book chapter or chapters for the current phase, which the human will provide in the session.

Always check the plan before starting work. The plan defines what is in scope for the current phase. The chapter defines the patterns to implement.
