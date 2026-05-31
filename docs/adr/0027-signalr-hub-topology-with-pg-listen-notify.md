# 0027. SignalR Hub Topology with PostgreSQL LISTEN/NOTIFY Carrier

## Status

Accepted (May 2026)

## Context

The Phase 8 dashboards live-update through SignalR. Phase 7 shipped four polling-based dashboards (OrderDetail, OrderCreate wizard, InventoryDashboard's shared loop for Create and Adjust); Phase 8 retrofits them to push transport plus adds a customer-facing tracking dashboard at `/track/{orderId}` and a SaaS admin dashboard at `/admin/dashboard`.

The architecture has three independent design dimensions: how the SignalR hub publishes (the transport contract between projections and the hub), how the hub subscribes (the group scope clients subscribe to), and how the hub broadcasts (rate-limiting and reconnection semantics). Each dimension has a manuscript-aligned production-grade choice and a teaching-friendly shortcut; per ADR 0025, the production-grade choice ships and the chapter's prose reconciles at Phase 14.

The codebase carries two existing LISTEN/NOTIFY consumers as precedent. `OutboxProcessor` in `src/Infrastructure/EventStore.Postgres/OutboxProcessor.cs` LISTENs on `outbox_pending` (migration 0005) and dispatches outbox rows on wake. `DelayQueueProcessor` in the same project LISTENs on `delayed_commands_pending` (migration 0008) and dispatches due rows on wake. Both use the same shape: dedicated long-lived `NpgsqlConnection`, `WaitAsync` loop, `OnNotification` handler with a `TaskCompletionSource` wake, reconnect-on-drop with a one-second delay, fallback timer for liveness if the listener drops. The two share the empty-payload convention: the notification is a wake signal, the reader queries the table for row data.

Hosts.Web currently holds zero Postgres connections. The hub-and-publisher composition adds Hosts.Web's first Postgres dependency through a narrow LISTEN-only abstraction, not a full event-store reference.

ADR 0026 settled an analogous placement asymmetry for `CommandTypeRegistry` (Domain.Abstractions, persistence consumer drives placement below Application) versus `QueryTypeRegistry` (Application, transport-only consumer set permits Application placement). The same asymmetric-consumer-sets reasoning applies to the two new ports Phase 8 introduces: `INotificationPublisher` has a persistence consumer (six projection unit-of-work files), so Domain.Abstractions is correct; `IHubBackplaneConnection` has only a Hosts consumer, so Application is correct. The asymmetry is identical in shape to ADR 0026's.

## Decision

### The publication transport: pg_notify from inside the projection unit-of-work transaction

Each projection unit-of-work publishes a `NotificationEnvelope` via `pg_notify` inside its existing commit transaction. `pg_notify` is transactional in PostgreSQL: notifications are delivered to listeners at `COMMIT` time, and rollback suppresses delivery. The unit-of-work owns the `NpgsqlTransaction`; the notification rides the same transaction. A projection that commits a row change without notifying, or notifies without committing, is impossible by construction.

The channel name is `projection_committed`. Channels are conventions in PostgreSQL, not schema artifacts; no migration ships for this channel. No trigger fires the notification, in contrast to migrations 0005 and 0008 which use `AFTER INSERT` statement-level triggers calling `pg_notify` with an empty payload. The projection unit-of-work calls `pg_notify` directly with the envelope as the payload.

The notification envelope is a `NotificationEnvelope` record carrying `(string ProjectionName, string ResourceId, string EventName, IReadOnlyList<string> Widgets)`. Serialized JSON stays well under PostgreSQL's 8000-byte NOTIFY payload cap (a typical envelope is 100-300 bytes); the publisher enforces the cap with a named exception at the boundary that owns it, per ADR 0025's named-exceptions discipline.

The envelope carries the projection name, the resource identifier, the event that caused the change, and the affected widgets. That is enough for the hub to route to the correct group and for the page to decide whether to re-query. The envelope deliberately carries no row data: pages re-call the existing query handler on notification, per the notification-only-push pattern Chapter 13 §19b tier 3b names ("pub/sub stays cheap because it carries tiny messages; the store stays authoritative because values live there") and §19j antipattern #4 names ("unbounded real-time updates" via row-carrying messages).

This diverges from the outbox and delay-queue pattern, where the notification is a wake-only signal and the consumer queries the table. The divergence is intentional: outbox and delay-queue consumers process a queue of rows, where one notification per batch is the right signal density; the SignalR hub fans out per-event notifications to per-resource subscribers, where the envelope is the message and a follow-up read would round-trip through the event store for data that fits in a NOTIFY payload.

The `INotificationPublisher` port surface in `Domain.Abstractions` is `Task PublishAsync(NotificationEnvelope envelope, CancellationToken ct)`. No infrastructure types in the signature; the port is abstract at the layer it lives. The Postgres implementation arranges atomicity with the caller's commit through a transactional orchestration mechanism that lives in the Postgres adapter assembly: either a Postgres-specific overload on the concrete `PostgresPgNotifyPublisher` type that takes the `NpgsqlTransaction` directly, or a scoped ambient-transaction surface the unit-of-work registers and the publisher reads. The orchestration shape resolves at Commit 2 and Commit 3 pre-flight against the existing unit-of-work disk shapes; both shapes are production-grade and the choice is which fits the current adapter shape with least friction. The port surface is invariant across that choice.

### Per-resource group scope

SignalR groups are named per the resource shape:

- `order:{orderId}` for OrderDetail subscriptions and customer-tracking dashboard subscriptions.
- `inventory:{sku}` for InventoryDashboard per-SKU subscriptions.
- `admin:metrics` for the admin dashboard broadcast.
- `customer:{customerId}` reserved but unused in v1. The customer-tracking dashboard subscribes per-order rather than per-customer; multi-subscription patterns ("track all my orders") are Phase 14 polish per born-at-consumer discipline.

Per Chapter 13 §19h SaaS dashboard topic shape (`tenant:{id}:mrr`) and §19i customer dashboard topic shape (`order:{id}:updated`). Per-resource groups scale better than broadcast-and-client-filter at high subscriber counts and align with the manuscript's named pattern. The phase-8-readiness doc named broadcast-and-filter as simpler at v1's expected scale; per ADR 0025, the manuscript-aligned pattern is the production-grade choice and ships.

The publisher's `ResourceId` field maps directly to the resource portion of the group name: an envelope with `ProjectionName: "OrderDetail"` and `ResourceId: "{orderId}"` routes to `order:{orderId}`; an envelope with `ProjectionName: "InventoryDashboard"` and `ResourceId: "{sku}"` routes to `inventory:{sku}`. The hub's broadcast site holds the projection-name-to-group-prefix mapping; the publisher knows only the resource ID, not the group syntax.

### Snapshot-then-resume reconnection

On hub connect (and reconnect after a drop), the Blazor page first calls the existing query handler for authoritative state, then subscribes to the resource's group and processes notifications as they arrive. No client-side watermark deduplication. The notification → re-query pattern is naturally idempotent: re-query returns current state regardless of whether a notification was a duplicate, missed, or out-of-order.

Per Chapter 13 `ch13_dashboards_enduser_client_code`'s `onopen` handler triggering a full REST fetch. The phase-8-readiness doc's open question on watermark deduplication resolves to "no, the existing query handler is the watermark."

The hub itself has no buffer of recent notifications, no sequence numbers, no replay protocol. Notifications missed during a connection drop are recovered through the page's re-query on reconnect, not through hub-side state.

### Hub-side rate limiting

A per-group coalescing window at the hub's broadcast site collapses multiple notifications to the same resource within the window into a single broadcast. The last notification's payload wins (the envelope is small and order-independent at the resource level, so last-write-wins is honest).

Per Chapter 13 §19f.2 ("rate-limited updates ... cap visible updates at 2-4 per second per widget") and §19j antipattern #4 ("unbounded real-time updates"). The window value lies in the 250-500ms range matching the manuscript's "2-4 per second per widget" cap. The specific window value (250ms vs 500ms) resolves at Cluster 5 Commit 18 pre-flight against the rate-limiting test's behavior; both values fall within the manuscript's named range.

The rate-limiting applies per-group, not globally: two updates to different resources within the same window broadcast independently; two updates to the same resource within the window collapse.

### Port-placement asymmetry per ADR 0026 parallel

Two new ports land at Cluster 1, with placements determined by their consumer sets per ADR 0026's hexagonal-inversion reasoning:

- `INotificationPublisher` lives in `src/Domain.Abstractions/INotificationPublisher.cs`. Six unit-of-work files in `src/Infrastructure/ReadModels.Postgres/` consume it (`PostgresOrderListUnitOfWork`, `PostgresCustomerSummaryUnitOfWork`, `PostgresInventoryDashboardUnitOfWork`, `PostgresOrderDetailUnitOfWork`, `PostgresSkuToInventoryIdUnitOfWork`, `PostgresOrderIdToPaymentIdUnitOfWork`). Infrastructure consumers force Domain.Abstractions placement: Application placement would require Infrastructure to reference Application, inverting the layering rule. Same shape as ADR 0026's resolution for `CommandTypeRegistry`.

- `IHubBackplaneConnection` lives in `src/Application/SignalR/IHubBackplaneConnection.cs`. Only Hosts.Web's `DashboardHub` consumes it. No Infrastructure consumer surfaces. Application placement is correct: the consumer set is transport-only, the same as `QueryTypeRegistry`'s in ADR 0026. Domain.Abstractions placement would be premature in the same way ADR 0026 left `QueryTypeRegistry` in Application rather than promoting it.

The asymmetry between the two ports is the same shape ADR 0026 documented for `CommandTypeRegistry` (Domain.Abstractions, persistence consumer) versus `QueryTypeRegistry` (Application, transport-only consumer). ADR 0026's reasoning governs; this ADR cites it rather than re-deriving.

The `NotificationEnvelope` record lives in `src/Domain.Abstractions/NotificationEnvelope.cs` alongside the publisher port.

### Rejected alternatives

Three alternatives to the LISTEN/NOTIFY carrier were considered and rejected on production-quality grounds per ADR 0025:

- **HTTP hop from Workers to Hosts.Web's `/internal/notify` endpoint.** Workers would POST to a Web-host endpoint that broadcasts via `IHubContext<DashboardHub>`. Rejected: adds in-host coupling that LISTEN/NOTIFY does not require; does not scale to multiple Hosts.Web replicas (each replica would need to receive every notification independently, which LISTEN/NOTIFY handles natively through PostgreSQL's listener fanout); bypasses the pub/sub architecture Chapter 13 §19b tier 3b names.

- **Moving projection execution from Workers to Hosts.Web.** Projections would run inside the Hosts.Web process, with direct `IHubContext<DashboardHub>` access. Rejected: a bigger architectural change than Phase 8's scope warrants (Workers stays the projection home per Phase 6); mixes UI-host concerns with projection-host concerns; loses the operational isolation Workers provides.

- **Direct `IHubContext<DashboardHub>` injected into projection unit-of-work.** The unit-of-work would call the hub directly inside `CommitAsync`. Rejected: couples projections to SignalR specifically rather than to the publisher abstraction; violates the hexagonal layering rule (CLAUDE.md's "Domain depends on no I/O" and "Application depends on Domain and Domain.Abstractions only"); requires projections to know about a transport mechanism they should be agnostic to. The `INotificationPublisher` abstraction was rejected too in this option, since `IHubContext` is itself a transport-aware interface; abstracting it would re-derive the same indirection the publisher port provides.

## Consequences

- A new `INotificationPublisher` port lands in `src/Domain.Abstractions/` at Cluster 1 Commit 2, with `NotificationEnvelope` as a companion record. The port's surface is `Task PublishAsync(NotificationEnvelope envelope, CancellationToken ct)`; no infrastructure types appear in the signature.

- A new `PostgresPgNotifyPublisher` implementation lands in `src/Infrastructure/SignalR/` at Cluster 1 Commit 3. The implementation issues `NOTIFY projection_committed, '<envelope-json>'` against the caller's unit-of-work transaction, with the transactional-orchestration mechanism resolved at Commit 2 and Commit 3 pre-flight. Payload-size validation against PostgreSQL's 8000-byte cap surfaces at the boundary with a named exception.

- Six projection unit-of-work files in `src/Infrastructure/ReadModels.Postgres/` gain the publisher injection and the publish call inside `CommitAsync` at Cluster 1 Commit 5. All six land in one commit per ADR 0025's production-quality discipline: partial integration is a transitional state CI cannot meaningfully assert.

- A new `IHubBackplaneConnection` port lands in `src/Application/SignalR/` at Cluster 1 Commit 4, with `PostgresHubBackplaneConnection` and `HubBackplaneOptions` companions in `src/Infrastructure/SignalR/`. The backplane connection's shape parallels the outbox processor's listener: dedicated long-lived `NpgsqlConnection`, `WaitAsync` loop, `OnNotification` wake, reconnect-on-drop. The backplane has no idle-poll fallback: pages re-query on hub reconnect, so listener drops degrade gracefully through the snapshot-then-resume reconnection semantics rather than through hub-side polling.

- Hosts.Web gains its first Postgres dependency through `IHubBackplaneConnection`. The dependency is narrow: a LISTEN connection, not a full event-store reference. Npgsql joins Hosts.Web's package set; the event-store assembly does not.

- The SignalR hub class (`DashboardHub`) lands in `src/Hosts/Web/Hubs/` at Cluster 1 Commit 6, with DI registration and the `Microsoft.AspNetCore.SignalR.Client` package pin in `Directory.Packages.props`. The hub subscribes the backplane connection to `projection_committed`, deserializes received envelopes, applies per-group rate-limiting, and broadcasts to the matching group.

- Hub-side rate-limiting lands at Cluster 5 Commit 18 with the specific window value (250ms or 500ms) resolved at that commit's pre-flight. This ADR records the range and the rationale; the specific value is a Cluster 5 implementation choice.

- The shared `LiveBadge` Blazor component lands at Cluster 5 Commit 17 and is not Cluster 1 scope.

- The three retrofit sites (OrderDetail, OrderCreate wizard, InventoryDashboard shared loop) land at Cluster 2 Commits 7 through 9. The two new dashboards (customer-tracking, admin) land at Clusters 3 and 4.

- Structured logging at the publisher's emission site and the hub's receipt site provides the operational audit trail for notification delivery. The notification-only-push design (Chapter 13 §19b tier 3b) means notifications carry data directly with no backing table; a separate audit table would double the write per projection commit for content that structured logging discharges equivalently. Operator-facing observability follows the codebase's existing structured-logging pattern (correlation IDs flowing through where the underlying event carries them).

- Track A flag against Chapter 13's `ch13_dashboards_saas_query_code` and `ch13_dashboards_enduser_server_code`, which use raw `WebSocket` rather than SignalR. The reference implementation ships SignalR per PLAN.md; manuscript reconciliation at Phase 14 (F-0012-A).

- The `projection_committed` channel is a convention, not a schema artifact. No migration ships for Cluster 1.

## Trigger for revisiting

- A future projection or downstream consumer needs row data on the notification rather than a notification-then-requery shape. The current envelope deliberately carries no row data; a row-data-carrying envelope would push the architecture toward the antipattern Chapter 13 §19j #4 names. The trigger is a concrete consumer that cannot tolerate the re-query latency, not a theoretical optimization opportunity.

- Multi-Hosts.Web-replica scaling reveals that the per-replica LISTEN connection topology is insufficient (every replica independently receives every notification, which is the right shape for low-to-medium replica counts but becomes wasteful at high counts). The carrier would move to a fanout topology (Redis Pub/Sub, a SignalR Redis backplane, or similar) with the publisher abstraction's contract unchanged. The trigger is observed replica-count pressure, not a deployment-architecture preference.

- The 8000-byte NOTIFY payload cap proves insufficient for a future envelope shape. The current envelope is bounded by design (no row data) and a 100-300 byte typical size leaves substantial headroom. A future envelope that legitimately needs row data triggers both this revisit and the row-data revisit above; they are correlated triggers.

- A future port appears in `Application/SignalR/` or `Domain.Abstractions/` whose placement reasoning differs from this ADR's. This ADR records the per-port placement reasoning; a future port follows the same hexagonal-inversion logic from ADR 0026 rather than this ADR's specific resolution.

- A future event-store adapter (SQL Server, KurrentDB, DynamoDB per the Phase 2, Phase 10, and Phase 11 roadmap) needs a different notification carrier than `pg_notify`. The carrier choice is Postgres-specific; the publisher abstraction is adapter-agnostic, so an adapter-specific publisher implementation behind the same `INotificationPublisher` port satisfies the per-adapter need without reopening this ADR's hub-topology decisions.

- Customer-reported notification-delivery failures become a recurring operational concern that structured logging cannot adequately diagnose. The trigger is repeated cases where operators cannot answer "was the notification emitted?" from log data, not a theoretical observability preference. The remediation would be a backing audit table at the publisher's site or a hub-side delivery-confirmation surface; both are Phase 9 AdminConsole-scoped questions if they arise.

## Amendment: hub authentication and the subscription ownership check (P9.6)

P9.6 closes the direct-object-reference exposure the original hub carried: the subscription method trusted a client-supplied group behind a null-and-whitespace guard, and the hub route carried no authorization requirement, so any connected client could join any resource group and receive every future notification for a resource it had no right to see. P9.6 adds the route authorization requirement, authenticates the subscribing principal, and authorizes the subscription against the principal's permissions and resource ownership before the group join.

The hub route now requires an authenticated principal. The `/hubs/dashboard` endpoint carries the default authorization requirement, so an unauthenticated negotiate is rejected at the route rather than reaching a hub method. The cookie principal P9.3b established on the Blazor circuit is the authenticated identity, carried as the name-identifier claim on the hub caller context.

The subscription decision is made at the Api host, not in the Web host. The authorization substrate (the permission authorizer, the resource-ownership resolver, the authoritative-roles read, and the order-owner read model) lives in the Api host, composed there through the application and read-model registrations. The Web host is a thin Blazor relay that dispatches commands and queries over HTTP and holds no read-model query access of its own. Bringing the substrate into the Web host to authorize in process would have pulled the projection assembly into the UI host's deployment closure and put read-model credentials and connections in a second host, widening the read-isolation surface the multi-tenancy coverage mandate exists to confine. Instead the hub authorizes the subscription through the Api host over the same signed forwarded-identity channel every command and query already uses. This narrows the original consequence that recorded the Web host's Postgres dependency as a LISTEN connection only: the Web host gains no read-model query dependency, and the LISTEN-only backplane dependency is unchanged. The cost is one signed request per subscribe on a recoverable path; a failed authorize during an Api degradation degrades through the same snapshot-then-resume reconnection the design already relies on, since the page re-queries on reconnect.

A new gated endpoint answers the subscription authorization. It resolves the actor from the authenticated principal's name identifier, loads authoritative roles through the principal factory (never trusting the forwarded role claim, the same security choice the command and query edges make), and reproduces the query side's gate-and-ownership split: a permission gate to subscribe at all, then an ownership resolve for owner-scoped principals. For an order resource the gate is ViewOrder, an operational principal is one holding ViewCustomer (it may subscribe to any order), and an owner-scoped principal may subscribe only when the resolved owner customer id matches the order's owning customer id read from the order-detail read model. For an inventory resource the gate is ViewInventory and there is no ownership dimension, matching the query side's treatment of inventory as operational data with no owning customer. The endpoint returns a uniform allow-or-deny boolean for any authenticated caller. A not-owned order and a not-found order return a byte-identical denial, so a caller cannot tell a resource it may not see from one that does not exist, preserving the existence-hiding the query side's null-to-404 mapping provides.

The hub parses its own group string and sends the Api a resource-typed request, not the raw group string. The group-name syntax and the projection-name-to-group-prefix mapping stay entirely Web-side; the Api authorizes a resource, not a group, so it never acquires knowledge of the hub's SignalR group-naming convention. The hub maps the order and inventory prefixes to resource types and fails closed on any other prefix, including the reserved customer-per-id group and the admin-metrics group whose consumers are not built in v1. An unknown prefix and a malformed resource id are rejected at the hub before any Api call, so a malformed subscribe never reaches the Api.

The hub signs its outbound authorize request from the connection's actor, through a dedicated authorize client rather than the circuit-scoped client the Blazor pages use. The page client signs through a circuit-scoped identity provider, which has no circuit in a hub-method invocation; reusing it would resolve an unavailable identity. The dedicated client takes the actor id from the hub caller context and signs the forwarded value with the host's already-registered stateless signer, in the empty-roles canonical form the existing client uses, so the Api loads authoritative roles rather than trusting the forwarded set. A non-success response from the Api authorizes nothing: the client returns a denial rather than faulting the hub.

A denied subscription throws a named `UnauthorizedSubscriptionException`, which derives from `HubException` because that is the exception SignalR relays to the calling client, and the failure is owned at the hub boundary. Its message is uniform across the not-authorized, not-owned, and not-found cases, so the denial leaks nothing about why. The unsubscribe method stays ungated: a client may always drop its own subscription, and leaving a group exposes no resource it could not already leave.

Layered enforcement is proven by test, not asserted by reasoning. The endpoint integration tests run the real authorization substrate against a database for an owner allowed, a non-owner denied, a not-found order denied with a response byte-identical to the not-owned case, an operational principal allowed for any order, the inventory permission gate, and an unauthenticated request rejected at the route. The hub unit tests prove that a denial throws and the group join never runs, that a fail-closed prefix or a malformed id is rejected at the hub without an Api call, and that the route carries its authorization metadata. The within-tenant ownership case is what P9.6 closes; the cross-tenant subscription case is Phase 10, when the group names become tenant-qualified and the notification envelope carries the tenant.

The tenant-qualified group names and the envelope tenant field remain Phase 10. The group names are not globally unique across tenants (a SKU and the metrics group repeat across tenants; only the order GUID is unique), so the cross-tenant subscription rejection lands when the groups are tenant-qualified, not here. The loopback hub connection that will propagate the circuit identity to satisfy the route requirement from a Blazor page is built in the live-dashboards-completion phase, where its connection-authentication scheme is decided; P9.6 secures the hub surface ahead of that page-side consumer, and the tests drive the hub and the endpoint directly rather than through a live page connection.
