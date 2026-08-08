# Reference Implementation Build Plan

This document defines the scope and sequence for building the reference implementation that accompanies *Event Sourcing & CQRS* by Thomas Jaeger.

This is a Path 1 plan: the implementation matches the book's full Part 4 commitments. Four event stores as first-class peers (PostgreSQL hand-rolled, SQL Server hand-rolled, KurrentDB, DynamoDB), five aggregates across five bounded contexts, two process managers, four user-facing projections, full hexagonal layout, Blazor and JSON API, AdminConsole tools, and the test patterns from Chapter 16.

This is a solo build with Claude Code on the Max plan, run as an ordered sequence of phases. The authentication-and-authorization and multi-tenancy foundation phases, and the live-dashboards-completion phase (net of the Phase 8 work already delivered), were inserted ahead of the original downstream phases (AdminConsole through documentation). That insertion expands the original plan and is a real impact on the submission timeline, stated rather than absorbed silently.

**How to read a phase section.** Each phase section records what that phase set out to build at the time it was written. A completed phase's goals are a record of intent rather than a description of what currently ships, and the two can diverge without either being wrong. What currently ships is read from the code, with `docs/ARCHITECTURE.md` as the routing document for finding it.

This document is a living plan. Update the build log weekly with what was actually built, what changed, and what surprised you; the phase sections keep the intent they were written with. By the end of the build that log is itself launch-period content.

---

## Scope, locked

### What ships in v1

**Event stores.** Four implementations as peers behind a common abstraction:
- Hand-rolled PostgreSQL (the relational path)
- Hand-rolled SQL Server (the relational path on the Microsoft stack)
- KurrentDB via gRPC client (the specialized path)
- DynamoDB with conditional writes on the version attribute (the managed-cloud path)

Configuration switches between them with no domain-code changes.

**Projection trigger mechanisms.** One per event store, demonstrating the trade-offs:
- Polling and LISTEN/NOTIFY for PostgreSQL
- Polling for SQL Server
- Native catch-up subscriptions for KurrentDB
- DynamoDB Streams plus Lambda-equivalent for DynamoDB (LocalStack for local dev and integration tests)

**Aggregates.** Five aggregates across five bounded contexts:
- Sales: Order
- Fulfillment: Inventory, Shipment
- Billing: Payment
- Access: UserRoles
- Customer Support: no own aggregates (reads from others' projections)

**Process managers.** Two, both event-sourced themselves with their own streams:
- OrderFulfillmentProcessManager (the four-branch saga from Chapter 10, with all compensation paths)
- ReturnProcessManager (the smaller second example, different style for variety)

**Projections.** Four user-facing projections with both relational and document-shaped read models:
- OrderListProjection
- OrderDetailProjection
- CustomerSummaryProjection
- InventoryDashboardProjection

Four further projections register alongside these: SkuToInventoryIdProjection and OrderIdToPaymentIdProjection (the projection-private cross-aggregate lookups of ADR 0020), CurrentRolesProjection (the RBAC current-roles read model), and OrderThroughputProjection (the admin-metrics throughput meter from Phase 11), for eight registered projections in all.

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

**Test suite.** Test patterns from Chapter 16:
1. Given-When-Then aggregate tests
2. Projection tests
3. Process manager and saga tests
4. Property-based tests for invariants (FsCheck)
5. Property-based tests for serialization roundtrips
6. Replay tests against historical event streams
7. Integration tests with Testcontainers (PostgreSQL, SQL Server, KurrentDB) and LocalStack (DynamoDB)
8. Contract tests over the `IEventStore` port: one shared suite that every adapter runs

**Versioning.** One worked event-schema migration with a real upcaster (Chapter 11), demonstrating the upcasting pipeline and the migration playbook.

**Snapshots.** Snapshot pattern applied to the Order aggregate (Chapter 12), with snapshot-plus-tail-equals-full-replay tests.

**Migration tooling.** Standalone example separate from the main domain (Chapter 18):
- Simulated legacy CRUD database
- CDC pattern reading legacy table changes and emitting events
- Outbox-on-legacy pattern
- Strangler pattern showing legacy and event-sourced code coexisting
- Shadow mode emitting events in parallel with legacy writes and comparing them
- A README in the migration folder explaining each pattern and its trade-offs

**Infrastructure.** Docker Compose setup that brings up PostgreSQL, SQL Server, KurrentDB, and LocalStack with one command. It stands up the backing stores; the hosts are run separately with `dotnet run`. CI pipeline running the full test suite on every push.

**Documentation.** README that maps every chapter to its code. Architecture decision records (ADRs) for major choices. Cross-reference map between book chapters and code files at the front of each Part 4 chapter.

**Access control.** Role-based access control across the system. A permission model with a role-to-permission mapping, checked as permissions rather than role names. User-to-role assignments held in a small event-sourced Access context, so authorization changes are auditable; the role-to-permission mapping in startup-validated config. Command authorization as an Application pipeline behavior. Query and read-model authorization as role-and-ownership row filtering. Subscription authorization as a resource-ownership check. Real authentication at both hosts, establishing identity where the actor is currently hardwired empty, with the principal abstracted so an external identity provider can slot in later.

**Multi-tenancy.** Tenant isolation by a shared-schema discriminator, with read-isolation enforced by infrastructure (row-level security or a mandatory filter) rather than per-query discipline, and complete cross-tenant isolation tests at every boundary. Tenant context on the principal, in event metadata, and in stream identifiers. Tenant-scoped read models and dashboards. The existing event corpus migrated to a default tenant by an additive, append-only-respecting backfill. Tenant isolation enforced as an authorization boundary.

### Out of scope for v1

Marten as another event-store adapter for PostgreSQL. Marten is discussed in Chapter 8 as an alternative the reader could swap in for PostgreSQL, but is not implemented as a peer.

Redis, Elasticsearch, and S3 read models. Chapter 13 discusses these. The reference implementation uses PostgreSQL for read models, demonstrating the mixed pattern (DynamoDB or PostgreSQL event store paired with PostgreSQL read models).

Distributed messaging (RabbitMQ, Kafka). The reference implementation uses an in-process event bus driven by the outbox.

Access control and multi-tenancy are in scope; see the access-control and multi-tenancy entries in the v1 scope above. The residual boundaries the foundation design set: external identity-provider integration is out of scope (the principal is abstracted for it, but the integration itself is later work), and multi-region tenant placement and per-tenant data residency are out of scope unless a later need pulls them in.

External monitoring integrations (Prometheus, Grafana, CloudWatch). Metrics are exposed via simple endpoints, not pushed to external systems.

Production load testing, and performance testing of any kind. The snapshot mechanism's proof is a replay-count assertion rather than a timing one: a wall-clock budget on a shared CI runner is flaky by construction.

---

## Architectural decisions, locked

These decisions are made. Do not revisit unless something fundamental breaks.

| Decision | Choice | Source |
| --- | --- | --- |
| Domain | Order management retailer with five bounded contexts | Part 4, "The Domain" |
| Architecture style | Hexagonal (ports and adapters) | Part 4, "Solution Structure" |
| Event stores | PostgreSQL (hand-rolled), SQL Server (hand-rolled), KurrentDB, DynamoDB as peers | Part 4, "Technology Choices" |
| Read store | PostgreSQL with relational tables and JSONB | Part 4, "Technology Choices" |
| UI framework | Blazor Server | Part 4, "Web and API" |
| API framework | ASP.NET Core minimal APIs | Part 4, "Web and API" |
| Styling | Tailwind | Part 4, "Web and API" |
| Test framework | xUnit + FluentAssertions + Testcontainers + LocalStack | Part 4, "Technology Choices" |
| Property-based tests | FsCheck | Chapter 16 |
| Containerization | Docker Compose | Part 4, "Technology Choices" |
| .NET version | .NET 10 LTS, C# 14 (supported through November 10, 2028) | Part 4, "Technology Choices" |
| License | MIT | Book commitment |
| Repository host | github.com/ThomasJaeger | Book commitment |
| Tenant isolation model | Shared-schema discriminator with infrastructure-enforced read isolation | RBAC and multi-tenancy foundation |
| Authorization model | Permission-based; command authz as a pipeline behavior, query and read authz as row filtering, subscription authz as a resource-ownership check, identity from a real principal | RBAC and multi-tenancy foundation |
| TenantId type | Typed wrapper, a security-justified exception to the raw-Guid convention (amends ADR 0005's scope) | RBAC and multi-tenancy foundation |

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
    /Access             // Event-sourced user-to-role assignments
    /SharedKernel       // Money, Address, identifiers (Chapter 7 shared kernel)
  /Domain.Abstractions  // IEventStore, IEventStoreRepository, IDomainEvent, ports
  /Application
    /Commands           // Command types and handlers
    /Queries            // Query types and handlers
    /Middleware         // Logging, validation, idempotency
    /Pipelines          // Command and query pipeline behaviors
    /Authentication
    /Authorization
    /Context            // Ambient command, query, and tenant context
    /SignalR            // IHubBackplaneConnection
  /ProcessManagers
    /OrderFulfillment   // OrderFulfillmentProcessManager and its tests
    /Returns            // ReturnProcessManager and its tests
  /Projections
    /OrderList
    /OrderDetail
    /CustomerSummary
    /InventoryDashboard
    /CurrentRoles       // RBAC current-roles read model
    /SkuToInventoryId   // Projection-private lookup (ADR 0020)
    /OrderIdToPaymentId // Projection-private lookup (ADR 0020)
    /OrderThroughput    // Order-throughput meter
    /Infrastructure     // checkpointing, batched catch-up, failure handling
  /Infrastructure
    /EventStore.Postgres
    /EventStore.Postgres.Cli  // migrate entry point
    /EventStore.SqlServer
    /EventStore.Kurrent
    /EventStore.DynamoDb
    /EventStore.InMemory      // Test and demo adapter
    /Migrations.Postgres      // Engine-agnostic migration runner (ADR 0004)
    /ReadModels.Postgres
    /Outbox             // OutboxProcessor (PostgreSQL-resident outbox)
    /SignalR
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
  /Workers.Tests
  /Hosts.Web.Tests
  /Hosts.AdminConsole.Tests
  /Migration.Tests
  /EventStore.ContractTests   // The one suite all four adapters pass
  /TestInfrastructure         // Shared fixtures
/migrations             // SQL migrations applied in order
/docs                   // README, plan, build log, ADRs, chapter-to-code map
/docker                 // docker-compose.yml for the four backing services
/scripts                // manifest.sh
```

The folder names map to chapters. Domain shows Chapters 7 and 9. Application shows Chapters 8 and 13. ProcessManagers shows Chapter 10. Projections shows Chapter 13. Infrastructure shows Chapter 8 plus parts of 11, 12, 17. AdminConsole shows Chapter 17. Migration shows Chapter 18.

---

## Build sequence

Organized into 17 phases. The phases run sequentially; nothing in a later phase should appear in earlier phase output.

Each phase has scope, out-of-scope items, and done-when criteria. Pad the timeline if any phase runs over. Do not push the deadline by skipping the done-when criteria.

### Phase 1: Foundations

**Goals.**
- Solution structure created matching the layout above.
- `global.json` pinning the SDK to .NET 10 (with `rollForward: latestFeature`).
- Docker Compose file with PostgreSQL, SQL Server, KurrentDB, and LocalStack services running locally.
- Connection from .NET to all four services working with smoke tests.
- `migrations/` folder with the first event store schema migration for PostgreSQL.
- CI pipeline: build plus test on every push, against all four services. CI uses .NET 10 SDK.
- Domain.Abstractions populated with the core ports: `IAggregateRoot`, `IDomainEvent`, `IEventStore`, `IRepository<T>`.
- Common types defined: `EventId`, `StreamId`, `Version`, `EventEnvelope`, `EventMetadata` (CorrelationId, CausationId, OccurredAt, Actor).

**Out of scope.**
- Aggregate code. Phase 3.
- UI. Phase 7.
- KurrentDB and DynamoDB adapter implementation. Phases 13 and 14.
- `ISnapshotStore` port. Phase 15, with the snapshot pattern.
- The projection checkpoint port, which ships as `ICheckpointStore`. Phase 6, with the projection infrastructure.

**Done when.**
- `dotnet --version` inside the repo reports 10.0.x.
- `docker compose -f docker/docker-compose.yml up` brings up all four backing stores healthy.
- `dotnet test` runs and passes.
- CI is green on a pull request.
- The Domain.Abstractions interfaces are stable enough that the upcoming PostgreSQL adapter will fit them without redesign.

### Phase 2: PostgreSQL and SQL Server event stores and outboxes

**Goals.**
- `EventStore.Postgres` adapter implementing `IEventStore` with `AppendAsync(streamId, expectedVersion, events)` and `ReadStreamAsync(streamId, fromVersion)` and `ReadAllFromCheckpointAsync(checkpoint)`.
- `EventStore.SqlServer` adapter implementing the same `IEventStore` operations against SQL Server, as a parallel hand-rolled relational implementation. Sequenced as a separate session after the PostgreSQL adapter is green; per ADR 0004 the adapter is self-contained and does not share a relational layer with the PostgreSQL adapter.
- Optimistic concurrency via unique constraint on (StreamId, Version) in both adapters. Concurrent writes throw `ConcurrencyException` with stream and expected/actual version detail. Engine-specific unique-violation codes (SQLState 23505 for PostgreSQL, error 2627 for SQL Server) are translated inside each adapter.
- JSON serialization for event payloads with type-name resolution. Storage type differs per engine (JSONB for PostgreSQL, NVARCHAR(MAX) for SQL Server).
- Outbox table created in each adapter. `OutboxProcessor` drains the outbox to an in-process event bus.
- Atomic write: events table and outbox table updated in the same transaction inside each adapter.
- Integration tests with Testcontainers cover, for each adapter independently: append, read, concurrent appends, outbox drain, outbox idempotency under simulated failures.

**Out of scope.**
- Snapshots. Phase 15.
- Schema versioning of events. Phase 15.
- KurrentDB and DynamoDB adapters. Phases 13 and 14.
- Engine-native projection trigger mechanisms for SQL Server (Service Broker, Change Tracking). The SQL Server projection trigger is polling in v1; engine-native alternatives are deferred to a later session if they are added at all.

**Done when.**
- Both adapters pass the same suite of integration tests.
- Tests demonstrate that concurrent writes to the same stream version produce a clear `ConcurrencyException` in both adapters.
- Tests demonstrate read-after-write consistency in both adapters.
- Tests demonstrate that subscriber failures do not lose events from the outbox in both adapters.
- A simple harness can write events and observe them flow through the outbox in both adapters.
- Switching the configured event store from PostgreSQL to SQL Server in a test run requires no domain-code changes.

### Phase 3: Sales context (Order aggregate)

**Goals.**
- `Order` aggregate with full lifecycle: drafted, lines added and removed, shipping address set, placed, then shipped or cancelled.
- Events: OrderDrafted, OrderLineAdded, OrderLineRemoved, ShippingAddressSet, OrderPlaced, OrderCancelled, OrderShipped.
- Command methods: Draft (static factory), AddLine, RemoveLine, SetShippingAddress, Place, Ship, Cancel.
- `Apply(IDomainEvent)` reconstruction.
- Aggregate-level invariants enforced (lines change only while the order is a draft, an order cannot be placed without lines and a shipping address, a shipped order cannot be cancelled, etc.).
- Value objects: `Money`, `Address`, `OrderLine`.
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

### Phase 4: Other contexts (Inventory, Shipment, Payment)

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

### Phase 5: Process managers

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

### Phase 6: Projections

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
- KurrentDB native subscriptions. Phase 13.
- DynamoDB Streams. Phase 14.

**Stretch goal for the same phase.**
- Add LISTEN/NOTIFY-based projection trigger as an alternative to polling. Both work; both have tests. The polling implementation stays as the default to keep the architecture demonstrable on any database.

**Done when.**
- All four projections build correctly from a stream of events.
- Each projection can be rebuilt from scratch and arrives at the same state.
- The query handlers return correct data for each read model.

### Phase 7: Web and API

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
- Authentication and authorization. The RBAC foundation phase.

**Done when.**
- A user can place an order and watch it move through the lifecycle in the UI.
- All four failure categories produce clear, distinct UI feedback.
- The JSON API exposes equivalent operations.
- Submitting the same command twice with the same idempotency key produces the same effect once.

### Phase 8: Live dashboards and SignalR

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

**Status note.** Phase 8 delivered the SignalR hub and the LISTEN/NOTIFY backplane (Cluster 1, CI-green at bfb4727). The retrofit sites, the two new dashboards, the LiveBadge component, hub-side rate limiting, and the cross-tab verification moved to the live-dashboards-completion phase, because they depend on the authentication-and-authorization and multi-tenancy foundation. The two done-when criteria above move with them; the hub-broadcasts-on-projection-commit capability they imply is proven by Cluster 1.

Retired in Phase 11: the runtime hub was replaced by in-process notification dispatch (ADR 0032).

### Phase 9: Authentication and Authorization (RBAC)

**Goals.** A permission model with a startup-validated role-to-permission mapping, checked as permissions. User-to-role assignments in a small event-sourced Access context with a bootstrap administrator; the role-to-permission mapping in config. Command authorization as an Application pipeline behavior, folded after logging and before idempotency so an unauthorized command consumes no idempotency storage and reaches neither validation nor the handler. Every command declares its required permission, with startup validation failing loudly on a gap. Query and read-model authorization as role-and-ownership row filtering. SignalR subscription authorization at the hub (hub authentication plus the resource-ownership check that closes the direct-object-reference exposure). Real authentication at both hosts, establishing identity where the actor is hardwired empty, with the principal abstracted for a future external identity provider. Identity propagation through the async chains, with caused commands and resurfaced delayed commands authorizing under a system actor while preserving the originating correlation. A system role holding the permissions process managers exercise. Tests at every enforcement point, complete per the cross-tenant coverage mandate's authz boundaries.

**Out of scope.** External identity-provider integration (the principal is abstracted for it; the integration is later work).

**Done when.** A principal performs only the commands its roles permit. A principal sees only the read-model rows its roles and ownership allow. A subscription to a resource group is rejected unless the principal owns the resource. The actor is established from a real principal at both hosts and flows into event metadata, no longer hardwired empty. Caused commands authorize under the system actor. Every enforcement point has tests.

### Phase 10: Multi-tenancy

**Goals.** The shared-schema discriminator implemented end to end, with read-isolation enforced by infrastructure (a per-store tenant predicate that reads the current tenant, the mechanism recorded in ADR 0031, with row-level security available as a future defense-in-depth layer) rather than per-query discipline. A typed TenantId, with raw Guid retained elsewhere. Tenant context in event metadata (the EventMetadata change carried by both event envelopes) and in stream identifiers (the StreamId namespacing change, tenant-after-prefix so prefix-family routing is preserved). A tenant_id discriminator on every read-model table, and on the events table for operational tenant filtering and per-tenant replay. Tenant-scoped read models and tenant-qualified dashboard groups. The existing event corpus migrated to a default tenant by an additive backfill that leaves historical stream identifiers untouched and tolerates the legacy two-segment form. Tenant context propagated through commands, projections, process managers, the outbox, and the delay queue, set from the principal at the HTTP edge and from metadata at the worker edge. Complete, structurally-enforced cross-tenant isolation tests at every boundary: every query, command, subscription, and projection, the enforcement mechanism, the idempotency and delay-queue paths, and per-tenant rebuild.

**Out of scope.** Multi-region tenant placement and per-tenant data residency, unless design pulls them in.

**Done when.** A tenant's data is isolated from every other tenant's at the discriminator-plus-enforcement level. A query or subscription scoped to one tenant cannot observe another tenant's state. The corpus migration completes and historical events carry the default tenant. The tenant propagates through commands, projections, process managers, the outbox, and the delay queue. Cross-tenant isolation coverage is complete and structurally enforced at every boundary.

### Phase 11: Live Dashboards Completion

**Goals.** The deferred Phase 8 work, built authz-and-tenant-aware from the start. The three retrofit sites (OrderDetail, OrderCreate, InventoryDashboard) receive in-process notifications instead of polling. The two new dashboards (customer-facing order tracking, SaaS admin metrics). The shared LiveBadge connection-status component reflecting circuit and connection state. Per-subscriber bounded coalescing in the dispatcher (the in-process successor to the planned hub-side rate limiting). The cross-tab verification.

**Out of scope.** As the original Phase 8 scope named.

**Done when.** The original Phase 8 done-when criteria (an order placed in one tab updates the customer dashboard in another within seconds; the admin dashboard's metrics match the AdminConsole tools), now with each surface tenant-scoped and authorized.

**Status note.** Phase 11 is closed on its named closer, the live /admin/throughput meter (RED #7, commit e882b23). The second done-when criterion above, that the admin dashboard's metrics match the AdminConsole tools, carries forward to Phase 12 as an exit condition, because the AdminConsole host does not exist yet and there is no operator-tool side to compare against. The criterion moves with Phase 12, the same way Phase 8 moved its done-when into this phase.

### Phase 12: AdminConsole

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

**Decision note.** Operational metrics (projection lag, outbox depth, events per second) live in the AdminConsole, per ADR 0039, and stay out of the Web host throughput meter. The operational reader is Phase 12's first slice, born at the Projection Status Dashboard and tested standalone before the dashboard consumes it; projection lag is head position minus checkpoint position over the global read_models.projection_checkpoints table. The inherited Phase 11 cross-tab done-when, that the admin dashboard's metrics match the AdminConsole tools, is a Phase 12 exit condition.

### Phase 13: KurrentDB adapter

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

### Phase 14: DynamoDB adapter

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

**Refutation note.** The Global Secondary Index goal above is refuted rather than merely diverged. DynamoDB rejects a consistent read against a GSI outright, so a GSI-backed position cannot serve the strongly-consistent ordered read the commit-order invariant requires. Ordering rides a log partition on the base table instead: one row per committed event under a single partition key, the position as its sort key, read in sort order. ADR 0049 records the refutation, including why LocalStack would not have surfaced it. The goal stays as written because it records what Phase 14 set out to build; this note records that the mechanism it names cannot work.

### Phase 15: Versioning and snapshots

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

### Phase 16: Migration tooling

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

### Phase 17: Documentation, reconciliation, polish

**Goals.**
- Top-level README excellent. What the project demonstrates, how to run it, how it maps to chapters, how to extend.
- Chapter-to-code map document: every pattern in the book, where its code lives, in a single navigable index.
- Architecture decision records (ADRs) for every significant choice: hexagonal layout, four event stores, hand-rolled vs Marten, in-process bus vs distributed messaging, PostgreSQL read models only, etc.
- Manuscript reconciliation: walk through every chapter that references the reference implementation. Confirm references match what was actually built. Update manuscript where reality diverged. Update sample chapters if needed.
- Build log finalized.
- Code cleanup: TODO comments resolved or tracked.
- Final test run, full coverage report.
- Tag v1.0.0 release on GitHub.
- Update proposal package's supplementary materials description with the GitHub URL and a brief summary of what is in the repo.

**Note on prior reconciliation work.** The .NET 10 / C# 14 manuscript update was completed in Track A in April 2026, ahead of Phase 17. ADR 0001 in this repo records the decision and its closure. Phase 17 reconciliation focuses on whatever divergences accumulate during Phases 2-16.

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

**Bring the relevant chapter into context.** Each phase corresponds to one or two chapters. When starting Phase 5 (process managers), have Chapter 10 available. When starting Phase 15 (versioning and snapshots), have Chapters 11 and 12 available. The book's specific patterns belong in Claude Code's working memory while you build.

**Do not let scope expand within a phase.** Each phase has a done-when criterion. When the criterion is met, stop. Do not let "while we're here" additions creep in. The next phase is two weeks away; the work will fit there.

**End sessions deliberately.** Token usage on the Max plan is generous but not unlimited. Long idle conversations consume context without producing work. End a session when work pauses; start a fresh one when you return.

**Update the build log weekly.** End each week (or each phase) by appending a short note to the build log: what got built, what changed, what surprised you. Ten minutes of writing per week becomes hours of valuable launch content by the end of the build.

**Commit small.** Commit per logical unit of work, not per phase. Small commits make Claude Code sessions easier to recover from and make the eventual book-to-code references precise.

---

## Build log

### Phase 1
*To be filled in.*

### Phase 2
*To be filled in.*

### Phase 3
*To be filled in.*

(Continue per phase.)

---

## Risks and watchpoints

**Phase 2's SQL Server adapter is the first abstraction stress test.** Adding a second relational adapter alongside the PostgreSQL adapter forces `IEventStore` to handle two engines before the more-different KurrentDB and DynamoDB adapters arrive in Phases 13 and 14. If the abstraction has leaked PostgreSQL-specific concepts, this is where it shows up first. Treat any awkwardness in the SQL Server adapter as a signal about the abstraction, not the adapter.

**The RBAC and multi-tenancy foundation is the highest-risk work in the plan.** The EventMetadata and StreamId changes ripple through every layer. The two unbuilt event-store adapters (KurrentDB and DynamoDB) must be built tenant-aware from the start, or they incur the re-key cost the sequencing exists to avoid. Cross-tenant leakage is a security boundary, so a tenancy bug is an incident rather than an ordinary defect: under the discriminator a single missing tenant predicate is a one-line breach, which is why read-isolation is enforced by infrastructure and the cross-tenant coverage is complete and structural rather than discipline-dependent. The corpus migration is a one-time operation that must be correct; the additive, append-only-respecting backfill (historical stream identifiers untouched, the legacy form tolerated) is the lower-risk path chosen for it. The tenant threads through more accepted infrastructure than first anticipated: the async-propagation work touches the caused-command dispatch fragment (ADR 0014, anticipated by its own revisit-trigger), the delay-queue table (ADR 0017), and the command-idempotency key (ADR 0016, the key becoming tenant-scoped), each a touchback to an accepted ADR and each a place a tenancy bug would be a cross-tenant defect.

**Phases 13 and 14 are the highest-risk.** Adding KurrentDB and DynamoDB adapters after the relational adapters are mature is the moment when the abstraction in Domain.Abstractions is tested against fundamentally different storage models. If the abstraction was wrong, this is when it surfaces, and fixing it requires touching everything that depends on it. Pace these phases carefully and resist the temptation to skip them or simplify the test suite for them.

**Process managers are the second-highest risk.** Chapter 10 covers a lot of ground. Compensation branches, idempotency, timeouts, distributed coordination, observability. Phase 5's two weeks may run long. If it does, take the third week. Better to ship a correct OrderFulfillmentProcessManager than a buggy one that the book has to apologize for.

**Snapshot tests are deceptively hard.** "Snapshot plus tail equals full replay" sounds simple. Property-based tests on this property tend to surface subtle bugs in event-application order, in serialization, in timestamp handling. Plan time for surprises in Phase 15.

**Manuscript reconciliation in Phase 17 will take longer than expected.** Six months of building will produce dozens of small divergences from the manuscript. Each is a small edit; the aggregate is real work. Do not skimp.

**The temptation to keep building beyond v1.** Once the implementation runs, ideas for additional features will arrive faster than time allows. Resist them. The book is the product. v1 is what the book commits to. Anything beyond v1 is post-launch material, not pre-submission material. The RBAC and multi-tenancy foundation is the exception that proves the rule: a deliberate, sanctioned scope expansion driven by a direct production need and recorded in this plan's amendment, not the unsanctioned creep this entry warns against. The warning continues to apply to genuine scope creep.

---

## Done definition for v1

The reference implementation is done when:

1. Every required component in the scope section is built and tested.
2. CI is green on a clean clone.
3. `docker compose -f docker/docker-compose.yml up`, then the hosts started per the README, followed by browser navigation produces a working UI within five minutes of clone.
4. The README maps every chapter to its code.
5. The chapter-to-code map document covers every pattern in the book.
6. The manuscript and the code agree, including the chapters the book grows to teach access control and multi-tenancy; the parallel book-repo planning scopes that growth against the locked foundation design.
7. ADRs document the major architectural choices.
8. v1.0.0 is tagged on GitHub.
9. All four event store adapters pass the same test suite.
10. The proposal package's supplementary materials description references the actual GitHub URL.
11. No query, command, subscription, or projection reaches production without a cross-tenant isolation test, enforced structurally so a registered type that lacks coverage fails the suite.

When all eleven are true, the proposal goes to Pearson.

---

## After submission

The work does not end at v1.0.0. While Pearson reviews, the implementation continues to evolve in two ways.

**Defects and small improvements** that surface during the review get fixed promptly. Each fix is a commit, a small test addition, and possibly a small manuscript edit.

**Extension content for launch.** Companion blog posts, conference talk material, workshop curriculum, and the executive decks all draw on the implementation. The repo becomes the living center of the marketing plan.

The implementation is the book's anchor for years. Treat it as a long-lived asset, not a one-time deliverable.
