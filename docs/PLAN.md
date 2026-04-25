# Reference Implementation Build Plan

This document defines the scope, sequence, and weekly targets for building the reference implementation that accompanies *Event Sourcing & CQRS* by Thomas Jaeger.

This is a Path 1 plan: the implementation matches the book's full Part 4 commitments. Three event stores as first-class peers (PostgreSQL hand-rolled, KurrentDB, DynamoDB), five aggregates across four bounded contexts, two process managers, four projections, full hexagonal layout, Blazor and JSON API, AdminConsole tools, and the eleven test patterns from Chapter 16.

Realistic timeline: 24-28 weeks at 14 hours per week, solo, with Claude Code on the Max plan. Roughly six months from start to submission.

This document is a living plan. Update it weekly with what was actually built, what changed, and what surprised you. By week 28 it doubles as the build log, which is itself launch-period content.

---

## Scope, locked

### What ships in v1

**Event stores.** Three implementations as peers behind a common abstraction:
- Hand-rolled PostgreSQL (the relational path)
- KurrentDB via gRPC client (the specialized path)
- DynamoDB with conditional writes on the version attribute (the managed-cloud path)

Configuration switches between them with no domain-code changes.

**Projection trigger mechanisms.** One per event store, demonstrating the trade-offs:
- Polling and LISTEN/NOTIFY for PostgreSQL
- Native catch-up subscriptions for KurrentDB
- DynamoDB Streams plus Lambda-equivalent for DynamoDB (LocalStack for local dev and integration tests)

**Aggregates.** Five aggregates across four bounded contexts:
- Sales: Order
- Fulfillment: Inventory, Shipment
- Billing: Payment
- Customer Support: no own aggregates (reads from others' projections)

**Process managers.** Two, both event-sourced themselves with their own streams:
- OrderFulfillmentProcessManager (the four-branch saga from Chapter 10, with all compensation paths)
- ReturnProcessManager (the smaller second example, different style for variety)

**Projections.** Four named projections with both relational and document-shaped read models:
- OrderListProjection
- OrderDetailProjection
- CustomerSummaryProjection
- InventoryDashboardProjection

Read models live in PostgreSQL with a mix of relational tables and JSONB columns for document-shaped views.

**UI and API.** Two parallel surfaces, same command shapes:
- Blazor Server task-based UI (Chapter 15 patterns) with Tailwind for styling
- ASP.NET Core minimal-API endpoints exposing the same operations as JSON

**Cross-cutting middleware.** Logging, validation, idempotency-key enforcement.

**AdminConsole.** Operational tools from Chapter 17:
- Event Store Browser
- Correlation-ID Tracer
- Projection Status Dashboard
- Replay Tool

Deliberately rough, because the book argues the cheapest tools that solve the problem are the right ones.

**Test suite.** Eleven test patterns from Chapter 16:
1. Given-When-Then aggregate tests
2. Projection tests
3. Process manager and saga tests
4. Property-based tests for invariants (FsCheck)
5. Property-based tests for serialization roundtrips
6. Replay tests against historical event streams
7. Integration tests with Testcontainers (PostgreSQL, KurrentDB) and LocalStack (DynamoDB)
8. Contract tests between layers
9. Mutation testing on the domain (Stryker.NET)
10. Performance smoke tests
11. Chaos and failure injection tests

**Versioning.** One worked event-schema migration with a real upcaster (Chapter 11), demonstrating the upcasting pipeline and the migration playbook.

**Snapshots.** Snapshot pattern applied to the Order aggregate (Chapter 12), with snapshot-plus-tail-equals-full-replay tests.

**Migration tooling.** Standalone example separate from the main domain (Chapter 18):
- Simulated legacy CRUD database
- CDC pattern reading legacy table changes and emitting events
- Outbox-on-legacy pattern
- Strangler pattern showing legacy and event-sourced code coexisting

**Infrastructure.** Docker Compose setup that brings up PostgreSQL, KurrentDB, LocalStack, and the application processes with one command. CI pipeline running the full test suite on every push.

**Documentation.** README that maps every chapter to its code. Architecture decision records (ADRs) for major choices. Cross-reference map between book chapters and code files at the front of each Part 4 chapter.

### Out of scope for v1

Marten as a fourth event-store adapter. Marten is discussed in Chapter 8 as an alternative the reader could swap in, but is not implemented as a peer.

Redis, Elasticsearch, and S3 read models. Chapter 13 discusses these. The reference implementation uses PostgreSQL for read models, demonstrating the mixed pattern (DynamoDB or PostgreSQL event store paired with PostgreSQL read models).

Distributed messaging (RabbitMQ, Kafka). The reference implementation uses an in-process event bus driven by the outbox.

Multi-tenancy beyond a tenant ID column. Chapter 13 covers patterns; the implementation demonstrates the simplest form that supports the dashboards.

Authentication and authorization beyond a stub. Chapter 17 discusses operational concerns; the reference implementation does not ship with real auth.

External monitoring integrations (Prometheus, Grafana, CloudWatch). Metrics are exposed via simple endpoints, not pushed to external systems.

Production load testing. A performance smoke test demonstrates the snapshot speedup; nothing beyond that.

---

## Architectural decisions, locked

These decisions are made. Do not revisit unless something fundamental breaks.

| Decision | Choice | Source |
| --- | --- | --- |
| Domain | Order management retailer with four bounded contexts | Part 4, "The Domain" |
| Architecture style | Hexagonal (ports and adapters) | Part 4, "Solution Structure" |
| Event stores | PostgreSQL (hand-rolled), KurrentDB, DynamoDB as peers | Part 4, "Technology Choices" |
| Read store | PostgreSQL with relational tables and JSONB | Part 4, "Technology Choices" |
| UI framework | Blazor Server | Part 4, "Web and API" |
| API framework | ASP.NET Core minimal APIs | Part 4, "Web and API" |
| Styling | Tailwind | Part 4, "Web and API" |
| Test framework | xUnit + FluentAssertions + Testcontainers + LocalStack | Part 4, "Technology Choices" |
| Property-based tests | FsCheck | Chapter 16 |
| Mutation testing | Stryker.NET | Chapter 16 |
| Containerization | Docker Compose | Part 4, "Technology Choices" |
| .NET version | .NET 10 LTS, C# 14 (supported through November 10, 2028) | Part 4, "Technology Choices" |
| License | MIT | Book commitment |
| Repository host | github.com/ThomasJaeger | Book commitment |

The manuscript and the implementation agree on .NET 10 / C# 14 as of April 2026. The Track A pass updated Part 4 Technology Choices, Part 5 Resources, and the cross-references in other chapters. ADR 0001 in this repo records the original deviation and is now closed at superseded-by-manuscript status.

---

## Project layout

The solution layout reflects the manuscript's Part 4 description.

```
/src
  /Domain
    /Sales              // Order aggregate, events, value objects
    /Fulfillment        // Inventory, Shipment aggregates
    /Billing            // Payment aggregate
    /CustomerSupport    // Read-only context
    /SharedKernel       // Money, Address, identifiers (Chapter 7 shared kernel)
  /Domain.Abstractions  // IAggregateRoot, IDomainEvent, IRepository, ports
  /Application
    /Commands           // Command types and handlers
    /Queries            // Query types and handlers
    /Middleware         // Logging, validation, idempotency
  /ProcessManagers
    /OrderFulfillment   // OrderFulfillmentProcessManager and its tests
    /Returns            // ReturnProcessManager and its tests
  /Projections
    /OrderList
    /OrderDetail
    /CustomerSummary
    /InventoryDashboard
    /Infrastructure     // checkpointing, batched catch-up, failure handling
  /Infrastructure
    /EventStore.Postgres
    /EventStore.Kurrent
    /EventStore.DynamoDb
    /ReadModels.Postgres
    /Outbox             // OutboxProcessor (PostgreSQL-resident outbox)
    /Snapshots
    /Versioning         // upcasting pipeline, schema registry stub
  /Hosts
    /Web                // Blazor Server task-based UI
    /Api                // JSON API
    /Workers            // projection workers, outbox processor, process managers
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
/migrations             // SQL migrations applied in order
/docs                   // README, plan, build log, ADRs, chapter-to-code map
/docker                 // docker-compose.yml, Dockerfiles
```

The folder names map to chapters. Domain shows Chapter 9. Application shows Chapters 8 and 13. ProcessManagers shows Chapter 10. Projections shows Chapter 13. Infrastructure shows Chapter 8 and parts of Chapter 17. AdminConsole shows Chapter 17. Migration shows Chapter 18.

---

## Build sequence

Twenty-eight weeks total, organized into 14 phases of roughly two weeks each. The phases run sequentially; nothing in a later phase should appear in earlier phase output.

Each phase has scope, out-of-scope items, and done-when criteria. Pad the timeline if any phase runs over. Do not push the deadline by skipping the done-when criteria.

### Phase 1, Weeks 1-2: Foundations

**Goals.**
- Solution structure created matching the layout above.
- `global.json` pinning the SDK to .NET 10 (with `rollForward: latestFeature`).
- Docker Compose file with PostgreSQL, KurrentDB, and LocalStack services running locally.
- Connection from .NET to all three services working with smoke tests.
- `migrations/` folder with the first event store schema migration for PostgreSQL.
- CI pipeline: build plus test on every push, against all three services. CI uses .NET 10 SDK.
- Domain.Abstractions populated with the core ports: `IAggregateRoot`, `IDomainEvent`, `IEventStore`, `IRepository<T>`, `ISnapshotStore`, `IProjectionCheckpoint`.
- Common types defined: `EventId`, `StreamId`, `Version`, `EventEnvelope`, `EventMetadata` (CorrelationId, CausationId, OccurredAt, Actor).

**Out of scope.**
- Aggregate code. Phase 3.
- UI. Phase 7.
- KurrentDB and DynamoDB adapter implementation. Phases 10 and 11.

**Done when.**
- `dotnet --version` inside the repo reports 10.0.x.
- `docker compose up` brings up all three services healthy.
- `dotnet test` runs and passes.
- CI is green on a pull request.
- The Domain.Abstractions interfaces are stable enough that the upcoming PostgreSQL adapter will fit them without redesign.

### Phase 2, Weeks 3-4: PostgreSQL event store and outbox

**Goals.**
- `EventStore.Postgres` adapter implementing `IEventStore` with `AppendAsync(streamId, expectedVersion, events)` and `ReadStreamAsync(streamId, fromVersion)` and `ReadAllFromCheckpointAsync(checkpoint)`.
- Optimistic concurrency via unique constraint on (StreamId, Version). Concurrent writes throw `ConcurrencyException` with stream and expected/actual version detail.
- JSON serialization for event payloads with type-name resolution.
- Outbox table created. `OutboxProcessor` drains the outbox to an in-process event bus.
- Atomic write: events table and outbox table updated in the same transaction.
- Integration tests with Testcontainers cover: append, read, concurrent appends, outbox drain, outbox idempotency under simulated failures.

**Out of scope.**
- Snapshots. Phase 12.
- Schema versioning of events. Phase 12.
- KurrentDB and DynamoDB adapters. Phases 10 and 11.

**Done when.**
- Tests demonstrate that concurrent writes to the same stream version produce a clear `ConcurrencyException`.
- Tests demonstrate read-after-write consistency.
- Tests demonstrate that subscriber failures do not lose events from the outbox.
- A simple harness can write events and observe them flow through the outbox.

### Phase 3, Weeks 5-6: Sales context (Order aggregate)

**Goals.**
- `Order` aggregate with full lifecycle: Placed, PaymentAuthorized, InventoryReserved, Shipped, Completed, Cancelled.
- Events: OrderPlaced, OrderPaymentAuthorized, OrderInventoryReserved, OrderShipped, OrderCompleted, OrderCancelled.
- Command methods: PlaceOrder, RecordPaymentAuthorized, RecordInventoryReserved, MarkShipped, MarkCompleted, Cancel.
- `Apply(IDomainEvent)` reconstruction.
- Aggregate-level invariants enforced (cannot ship an unpaid order, cannot cancel a completed order, etc.).
- Value objects: `OrderId`, `Money`, `OrderLine`, `CustomerReference`.
- Given-When-Then unit tests covering happy path and every invariant violation.
- `OrderRepository` that loads the aggregate by replaying events from the EventStore through the PostgreSQL adapter.

**Out of scope.**
- Other aggregates. Phase 4.
- Process managers. Phase 5.
- UI. Phase 7.

**Done when.**
- All Order lifecycle transitions have tests.
- Every invariant has a test that fails when violated.
- Test class reads as documentation. A reader can follow the test names and understand the Order's behavior without reading the production code.
- The aggregate persists and rehydrates correctly through the OrderRepository against PostgreSQL.

### Phase 4, Weeks 7-8: Other contexts (Inventory, Shipment, Payment)

**Goals.**
- `Inventory` aggregate (Fulfillment context). Events: InventoryReserved, InventoryReleased, InventoryAdjusted.
- `Shipment` aggregate (Fulfillment context). Events: ShipmentScheduled, ShipmentDispatched, ShipmentDelivered, ShipmentReturned.
- `Payment` aggregate (Billing context). Events: PaymentAuthorized, PaymentCaptured, PaymentRefunded, PaymentVoided.
- Repositories for each.
- Given-When-Then tests for each aggregate's lifecycle and invariants.
- Cross-context vocabulary documented: where the same word means different things, with comments referencing Chapter 7.

**Out of scope.**
- Process managers coordinating across these. Phase 5.
- Projections. Phase 6.

**Done when.**
- All four aggregates (Order from Phase 3, plus these three) are complete with tests.
- Each aggregate persists and rehydrates correctly.
- The bounded context boundaries are visible in the code structure.

### Phase 5, Weeks 9-10: Process managers

**Goals.**
- `OrderFulfillmentProcessManager` event-sourced, with its own state stream.
- Receives events: OrderPlaced, PaymentAuthorized, InventoryReserved, ShipmentDispatched, ShipmentDelivered.
- Emits commands: AuthorizePayment, ReserveInventory, ScheduleShipment, MarkOrderCompleted.
- All four compensation branches implemented and tested:
  1. Cancel before payment authorization.
  2. Refund after payment but before inventory reserved.
  3. Release inventory after reservation but before shipment.
  4. Refund-and-release after shipment fails.
- Timeouts via a delay queue (PostgreSQL table holding scheduled commands).
- Idempotency keys on commands so retries do not produce duplicate effects.
- `ReturnProcessManager` for the returns workflow: delivered order returned, inventory released back to stock, customer refunded.
- Tests: feed events, assert on commands emitted plus internal state. Each compensation branch has a dedicated test.

**Out of scope.**
- Projections that observe these. Phase 6.
- UI for triggering. Phase 7.

**Done when.**
- The full happy path of OrderFulfillment runs end to end.
- Each compensation branch has at least one test that exercises it.
- A timeout test demonstrates that a process manager waiting for an event that never arrives correctly times out and triggers compensation.
- ReturnProcessManager runs through its happy path with tests.
- Replaying the same command twice produces the same result (idempotency verified).

### Phase 6, Weeks 11-12: Projections

**Goals.**
- `OrderListProjection`: simple list view of orders with status, customer name, total. Relational table.
- `OrderDetailProjection`: detailed view with line items and event timeline. Mix of relational and JSONB.
- `CustomerSummaryProjection`: per-customer aggregate stats (order count, lifetime value, last order date). Relational.
- `InventoryDashboardProjection`: per-product reservation and stock state. Relational.
- Each projection has a checkpoint stored in PostgreSQL.
- `ProjectionInfrastructure` module: generic checkpoint store, batched catch-up, retry on transient failure.
- Projection host process reads events from the PostgreSQL event store via polling on a configurable interval.
- Query handlers expose the read models: `GetOrderListQueryHandler`, `GetOrderDetailQueryHandler`, etc.
- Tests: feed event streams, assert on read models. Rebuild test for each projection (drop the read model, replay from the start, assert state matches).

**Out of scope.**
- LISTEN/NOTIFY trigger. Same phase, see below.
- KurrentDB native subscriptions. Phase 10.
- DynamoDB Streams. Phase 11.

**Stretch goal for the same phase.**
- Add LISTEN/NOTIFY-based projection trigger as an alternative to polling. Both work; both have tests. The polling implementation stays as the default to keep the architecture demonstrable on any database.

**Done when.**
- All four projections build correctly from a stream of events.
- Each projection can be rebuilt from scratch and arrives at the same state.
- The query handlers return correct data for each read model.

### Phase 7, Weeks 13-14: Web and API

**Goals.**
- `Web` host (Blazor Server) with task-based UI for the Order workflow.
- Each business operation maps to a named command (PlaceOrder, AuthorizePayment, Cancel, MarkShipped, ProcessReturn, etc.). No CRUD-shaped forms.
- Optimistic UI patterns from Chapter 15. Async feedback. Failure categorization (validation, business-rule, concurrency, infrastructure) with distinct UI treatments.
- Idempotency keys generated client-side and submitted with every command.
- Tailwind for styling.
- `Api` host (ASP.NET Core minimal APIs) exposing the same operations as JSON endpoints.
- Cross-cutting middleware: logging (with correlation IDs flowing into events), validation, idempotency-key enforcement.
- Tests: API integration tests asserting that the same operation through Web and API produces identical events.

**Out of scope.**
- Live dashboards. Phase 8.
- Authentication and authorization beyond a stub.

**Done when.**
- A user can place an order and watch it move through the lifecycle in the UI.
- All four failure categories produce clear, distinct UI feedback.
- The JSON API exposes equivalent operations.
- Submitting the same command twice with the same idempotency key produces the same effect once.

### Phase 8, Weeks 15-16: Live dashboards and SignalR

**Goals.**
- SignalR hub broadcasting projection updates as they happen.
- InventoryDashboard live view with WebSocket updates.
- Customer-facing order tracking dashboard with live status.
- SaaS admin dashboard showing system-level stats (events per second, projection lag, outbox depth).
- UI mockups from Chapter 13 implemented in Blazor with Tailwind.
- Tests: dashboard renders correctly with seeded events; SignalR hub broadcasts on projection updates.

**Out of scope.**
- Multi-tenant dashboards beyond the simplest tenant-ID column.

**Done when.**
- Placing an order in one browser tab updates the customer-facing dashboard in another tab within seconds.
- The admin dashboard shows live system metrics that match what the AdminConsole tools show.

### Phase 9, Weeks 17-18: AdminConsole

**Goals.**
- `Event Store Browser`: small Blazor page that lets you inspect any stream by ID, see all events, expand each event payload.
- `Correlation-ID Tracer`: query that finds all events with a given correlation ID across all streams. Output shows the chain: command, events, projection updates, follow-on commands.
- `Projection Status Dashboard`: per-projection checkpoint, lag in seconds, last error if any.
- `Replay Tool`: command-line utility that rebuilds a projection from scratch by deleting the read model and replaying events. Idempotent and safe to run.
- These tools deliberately rough. Function over polish, as Chapter 17 advocates.
- Tests for each tool against a seeded event log.

**Out of scope.**
- Production observability integrations. Metrics endpoints expose Prometheus-format text but no real Prometheus server is wired up in v1.

**Done when.**
- A reader can use the AdminConsole to investigate "what happened to order X" and trace the full chain.
- The Replay Tool successfully rebuilds each projection.
- The Projection Status Dashboard accurately reflects projection state.

### Phase 10, Weeks 19-20: KurrentDB adapter

**Goals.**
- `EventStore.Kurrent` adapter implementing `IEventStore` against KurrentDB via the gRPC client.
- Append, read, optimistic concurrency mapped to KurrentDB semantics.
- Configuration switch: same domain code, different event store, no code changes outside the infrastructure layer.
- Native catch-up subscription mechanism for projections, replacing polling when KurrentDB is the configured store.
- Integration tests against KurrentDB via Testcontainers.
- Documentation of trade-offs in code comments and ADR.

**Out of scope.**
- KurrentDB-specific features beyond what the abstraction needs. The point is the abstraction works.

**Done when.**
- All existing aggregate, projection, and process manager tests pass with the configuration switched to KurrentDB.
- Native subscriptions feed projections without polling.
- The Event Store Browser works against KurrentDB.

### Phase 11, Weeks 21-22: DynamoDB adapter

**Goals.**
- `EventStore.DynamoDb` adapter implementing `IEventStore` against DynamoDB.
- Composite key: partition key = `AggregateType#AggregateId`, sort key = Version.
- Conditional write with `attribute_not_exists(Version)` for optimistic concurrency.
- Global Secondary Index for global ordering and replay.
- Configuration switch: same domain code, different event store.
- DynamoDB Streams plus a stream consumer (LocalStack Lambda equivalent) feeding projections.
- Integration tests against LocalStack.
- Documentation of trade-offs in code comments and ADR.

**Out of scope.**
- Real AWS deployment. Local-only via LocalStack.
- DynamoDB-specific features beyond what the abstraction needs.

**Done when.**
- All existing aggregate, projection, and process manager tests pass with the configuration switched to DynamoDB via LocalStack.
- DynamoDB Streams feeds projections without polling.
- The Event Store Browser works against DynamoDB.
- The book's claim that switching event stores is a configuration change is now true.

### Phase 12, Weeks 23-24: Versioning and snapshots

**Goals.**
- One worked event versioning example: a real change to an Order event between v1 and v2.
- `Upcaster<TFrom, TTo>` infrastructure with chaining (v1 to v2 to v3 if needed).
- Upcasting pipeline runs at read time, never mutates stored events.
- Schema registry as a small in-process registry of known event schemas. Not a full schema registry server, just enough to demonstrate the pattern.
- Snapshot pattern applied to the Order aggregate.
- Snapshot trigger: every 50 events.
- Snapshot storage: separate PostgreSQL table.
- Snapshot tests: snapshot-plus-tail equals full-replay; snapshot reduces rehydration time on long streams measurably.
- Snapshot versioning: when a snapshot's schema changes, old snapshots are discarded and rebuilt rather than upcast.

**Out of scope.**
- Full schema registry server (Confluent-style). Chapter 11 discusses; v1 implements the in-process pattern only.

**Done when.**
- Old events with v1 schema rehydrate correctly through the upcaster after the schema change.
- Snapshot tests demonstrate equivalence and speedup.
- The book's worked example in Chapter 11 corresponds to runnable code.

### Phase 13, Weeks 25-26: Migration tooling

**Goals.**
- Standalone example separate from the main domain.
- Simulated legacy CRUD database (a small SQL schema representing a CRUD-shaped order system).
- CDC pattern: a process reads legacy table changes from a change-tracking table and emits domain events.
- Outbox-on-legacy pattern: legacy code path that writes to an outbox table inside the legacy database, with an event-emitter draining it.
- Strangler pattern example: a feature implemented twice, once in legacy CRUD and once in event-sourced code, with traffic routing between them.
- Shadow mode example: events emitted in parallel to legacy writes, compared for correctness.
- README in the migration folder explaining each pattern, when to use it, and trade-offs.

**Out of scope.**
- Real production migration scenarios. The example is a teaching artifact.

**Done when.**
- The migration folder runs as its own demo with `docker compose up`.
- A reader can run it and watch CRUD changes turn into events through each pattern.
- Each pattern has at least one test demonstrating correctness.

### Phase 14, Weeks 27-28: Documentation, reconciliation, polish

**Goals.**
- Top-level README excellent. What the project demonstrates, how to run it, how it maps to chapters, how to extend.
- Chapter-to-code map document: every pattern in the book, where its code lives, in a single navigable index.
- Architecture decision records (ADRs) for every significant choice: hexagonal layout, three event stores, hand-rolled vs Marten, in-process bus vs distributed messaging, PostgreSQL read models only, etc.
- Manuscript reconciliation: walk through every chapter that references the reference implementation. Confirm references match what was actually built. Update manuscript where reality diverged. Update sample chapters if needed.
- Build log finalized.
- Code cleanup: TODO comments resolved or tracked.
- Final test run, full coverage report.
- Tag v1.0.0 release on GitHub.
- Update proposal package's supplementary materials description with the GitHub URL and a brief summary of what is in the repo.

**Note on prior reconciliation work.** The .NET 10 / C# 14 manuscript update was completed in Track A in April 2026, ahead of Phase 14. ADR 0001 in this repo records the decision and its closure. Phase 14 reconciliation focuses on whatever divergences accumulate during Phases 2-13.

**Out of scope.**
- Marketing copy in the README. Keep it factual and useful.

**Done when.**
- A reader who has never seen the project can clone it, run it, and find the code for any chapter within five minutes.
- The manuscript and the code agree.
- The proposal is ready to send to Pearson.

---

## Working with Claude Code

The Max plan supports the work, but a few habits make sessions more productive.

**Start each session with the right context.** Load CLAUDE.md and this plan into the conversation. Identify the current phase and what is in scope for it. Tell Claude Code explicitly: "We are working on Phase N. Scope is Y. Do not pull patterns from later phases." This prevents drift.

**Bring the relevant chapter into context.** Each phase corresponds to one or two chapters. When starting Phase 5 (process managers), have Chapter 10 available. When starting Phase 12 (versioning and snapshots), have Chapters 11 and 12 available. The book's specific patterns belong in Claude Code's working memory while you build.

**Do not let scope expand within a phase.** Each phase has a done-when criterion. When the criterion is met, stop. Do not let "while we're here" additions creep in. The next phase is two weeks away; the work will fit there.

**End sessions deliberately.** Token usage on the Max plan is generous but not unlimited. Long idle conversations consume context without producing work. End a session when work pauses; start a fresh one when you return.

**Update the build log weekly.** End each week (or each phase) by appending a short note to the build log: what got built, what changed, what surprised you. Ten minutes of writing per week becomes hours of valuable launch content by week 28.

**Commit small.** Commit per logical unit of work, not per phase. Small commits make Claude Code sessions easier to recover from and make the eventual book-to-code references precise.

---

## Build log

### Phase 1, Weeks 1-2
*To be filled in.*

### Phase 2, Weeks 3-4
*To be filled in.*

### Phase 3, Weeks 5-6
*To be filled in.*

(Continue per phase.)

---

## Risks and watchpoints

**Phases 10 and 11 are the highest-risk.** Adding KurrentDB and DynamoDB adapters after the PostgreSQL adapter is mature is the moment when the abstraction in Domain.Abstractions is tested. If the abstraction was wrong, this is when it surfaces, and fixing it requires touching everything that depends on it. Pace these phases carefully and resist the temptation to skip them or simplify the test suite for them.

**Process managers are the second-highest risk.** Chapter 10 covers a lot of ground. Compensation branches, idempotency, timeouts, distributed coordination, observability. Phase 5's two weeks may run long. If it does, take the third week. Better to ship a correct OrderFulfillmentProcessManager than a buggy one that the book has to apologize for.

**Snapshot tests are deceptively hard.** "Snapshot plus tail equals full replay" sounds simple. Property-based tests on this property tend to surface subtle bugs in event-application order, in serialization, in timestamp handling. Plan time for surprises in Phase 12.

**Manuscript reconciliation in Phase 14 will take longer than expected.** Six months of building will produce dozens of small divergences from the manuscript. Each is a small edit; the aggregate is real work. Do not skimp.

**The temptation to keep building beyond v1.** Once the implementation runs, ideas for additional features will arrive faster than time allows. Resist them. The book is the product. v1 is what the book commits to. Anything beyond v1 is post-launch material, not pre-submission material.

---

## Done definition for v1

The reference implementation is done when:

1. Every required component in the scope section is built and tested.
2. CI is green on a clean clone.
3. `docker compose up` followed by browser navigation produces a working UI within five minutes of clone.
4. The README maps every chapter to its code.
5. The chapter-to-code map document covers every pattern in the book.
6. The manuscript and the code agree.
7. ADRs document the major architectural choices.
8. v1.0.0 is tagged on GitHub.
9. All three event store adapters pass the same test suite.
10. The proposal package's supplementary materials description references the actual GitHub URL.

When all ten are true, the proposal goes to Pearson.

---

## After submission

The work does not end at v1.0.0. While Pearson reviews, the implementation continues to evolve in two ways.

**Defects and small improvements** that surface during the review get fixed promptly. Each fix is a commit, a small test addition, and possibly a small manuscript edit.

**Extension content for launch.** Companion blog posts, conference talk material, workshop curriculum, and the executive decks all draw on the implementation. The repo becomes the living center of the marketing plan.

The implementation is the book's anchor for years. Treat it as a long-lived asset, not a one-time deliverable.
