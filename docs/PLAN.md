# Reference Implementation Scope

This document defines the scope of the reference implementation that accompanies *Event Sourcing &
CQRS* by Thomas Jaeger, the architectural decisions locked before the build, the project layout, and
the definition of done the v1 release was measured against.

The implementation matches the book's full Part 4 commitments: four event stores as first-class
peers (PostgreSQL hand-rolled, SQL Server hand-rolled, KurrentDB, DynamoDB), five aggregates across
five bounded contexts, two process managers, eight projections, a full hexagonal layout, Blazor and
JSON API hosts, AdminConsole operator tools, and the test patterns from Chapter 16.

**Scope is a record, not a description of what ships.** The scope below states what the build set out
to deliver. What currently ships is read from the code, with `docs/ARCHITECTURE.md` as the routing
document for finding it, `docs/chapter-to-code-map.md` for the chapter-to-code index, and the
fifty-four records in `docs/adr/` for why each decision went the way it did.

---

## Scope, locked

### What ships in v1

**Event stores.** Four implementations as peers behind a common abstraction:
- Hand-rolled PostgreSQL (the relational path)
- Hand-rolled SQL Server (the relational path on the Microsoft stack)
- KurrentDB via gRPC client (the specialized path)
- DynamoDB with conditional writes on the version attribute (the managed-cloud path)

Configuration switches between them with no domain-code changes. That holds for the two hosts
that compose an event store and serve the application, Api and Workers. The AdminConsole is the exception and refuses to compose on any
engine but PostgreSQL: its three read-side ports are hand-rolled PostgreSQL, and it fails at
startup with the provider named rather than booting green and failing at the first click.
`src/Hosts/AdminConsole/Program.cs` carries the refusal and the reasoning.

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
4. Property-based tests over the upcaster pipeline's topology and the shadow comparator (FsCheck)
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

The manuscript and the implementation agree on .NET 10 / C# 14 as of April 2026. The manuscript pass updated Part 4 Technology Choices, Part 5 Resources, and the cross-references in other chapters. ADR 0001 in this repo records the original deviation and is now closed at superseded-by-manuscript status.

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
/docs                   // scope, architecture, ADRs, chapter-to-code map
/docker                 // docker-compose.yml for the four backing services
/scripts                // check-plan-citations.sh
```

The folder names map to chapters. Domain shows Chapters 7 and 9. Application shows Chapters 8 and 13. ProcessManagers shows Chapter 10. Projections shows Chapter 13. Infrastructure shows Chapter 8 plus parts of 11, 12, 17. AdminConsole shows Chapter 17. Migration shows Chapter 18.

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
10. The repository is public at the URL the book gives its readers.
11. No query, command, subscription, or projection reaches production without a cross-tenant isolation test, enforced structurally so a registered type that lacks coverage fails the suite.

When all eleven are true, v1 is done.

---

