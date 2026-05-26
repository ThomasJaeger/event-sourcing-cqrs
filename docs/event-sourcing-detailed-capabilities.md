## Business functions / capabilities

These are the user-or-business-visible outcomes event sourcing and CQRS make possible. Not features of the pattern; outcomes the pattern enables that competing patterns make hard or impossible.

**Audit and compliance.** Every state change is a permanent record, time-stamped and attributable. Regulators, auditors, and internal compliance teams can answer "what happened, when, by whom, and why" for any business object without forensic reconstruction. Industries with retention mandates (finance, healthcare, government, insurance) meet them from the event log itself rather than from a parallel audit log they build and maintain.

**Temporal queries and historical accuracy.** Answering "what did this customer's order look like on March 15?" or "what was our inventory position at end-of-quarter?" becomes a natural operation rather than a data-recovery exercise. Backdated reporting, point-in-time disclosures, and "as-of" analysis run against the event stream.

**Business intelligence on actual behavior.** Traditional systems record current state; event-sourced systems record behavior over time. Analytics can answer "how often do customers add items then remove them before checkout," "what's the average time from cart creation to abandonment," "which products get reserved but never shipped." These questions are unanswerable against a state-only store.

**Forensics and root-cause analysis.** When something goes wrong in production (a bad order, a financial discrepancy, a customer complaint), the event log shows exactly what happened. No "the database says X but the customer claims Y" disputes. The events are the ground truth.

**Customer-facing transparency.** Order tracking, transaction history, and activity timelines (the "show me what's happened with my account" features users expect) need no separate modeling. The events are already the timeline; the UI just renders them.

**Compensation and correction.** Mistakes in event-sourced systems are corrected by emitting compensating events, not by mutating history. This produces an honest record of "we made a mistake on date X and corrected it on date Y" rather than the silent rewrites that erode trust in traditional systems.

**Decoupled scaling of reads and writes.** CQRS lets read-heavy and write-heavy parts of the business scale independently. A reporting workload that runs 1000 reads per write doesn't compete with the transactional path; each side scales to its own demand curve.

**Multiple specialized views of the same data.** The same events feed an order-tracking view, a fulfillment dashboard, a finance reconciliation report, a customer-service search index, a machine-learning training set. Each view is purpose-built for its consumer; no shared schema compromises serve everyone poorly.

**Eventual rebuild and refactor capability.** Projections (read models) can be deleted and rebuilt from the event log. Schema mistakes, new business questions, and additional reporting needs are all addressable by writing a new projection that replays history. The traditional "we need to backfill this column from scratch" migration nightmare doesn't apply.

**Business-process visibility and orchestration.** Process managers (sagas) coordinate multi-step business workflows (order-to-cash, return-to-refund, onboarding sequences) with explicit, traceable state. "Where is order 12345 in the fulfillment process?" answers from the process manager's state, not from inferring from database rows.

**Integration without coupling.** Events are the natural integration boundary. New systems consume the event stream without the originating system knowing they exist. Marketing automation, fraud detection, and customer success tooling all wire in by subscribing to existing events rather than by demanding API changes.

**Resilience and recovery posture.** The event log is the source of truth; everything else is derived. Cache corruption, projection corruption, partial outages, and replication lag are all recoverable by rebuilding from the log. The system has a known-good ground truth to recover toward.

**ML and AI readiness.** Training data for predictive models needs time-ordered historical data with behavioral detail. Event streams are that, natively. Traditional state-store systems require building event capture as a separate (expensive, often incomplete) project before ML work can begin.

**Regulatory adaptability.** When regulations change (new disclosure requirements, new retention rules, new audit categories), the existing event log usually has the data; only new projections need building. Traditional systems often discover the regulator wants data the system never captured.

## Technology / architecture roadmap items

These are the patterns event sourcing and CQRS pair with, enable, or naturally lead to. Roadmap-relevant in the sense that adopting ES/CQRS opens doors to or simplifies the adoption of each.

**Microservices and bounded contexts.** Event sourcing enforces clear ownership of state per aggregate; CQRS clarifies the read-side / write-side boundary. Together they make the per-service boundaries that microservices need explicit and enforceable. Domain-driven design's bounded contexts map cleanly to ES/CQRS service boundaries.

**Event-driven architecture (EDA).** Events as the integration currency between services. Pub-sub, event buses, message brokers, and Kafka-style streams are all natural fits. The event stream a service produces internally for its own state can be the same stream external consumers subscribe to.

**Reactive and streaming architectures.** Real-time projections, continuous queries, dashboards updating as events flow, streaming analytics on event topics. Reactive Extensions, Akka Streams, Kafka Streams, and Apache Flink all consume event streams natively.

**CDC (Change Data Capture) and data mesh.** Event sourcing makes change capture explicit at the source rather than reverse-engineered from database logs. Feeds data mesh patterns where each domain publishes its own data product (the event stream) for consumption by other domains.

**Polyglot persistence.** Read models can live in databases optimized for their access pattern (PostgreSQL for transactional reads, Elasticsearch for search, Redis for caching, TimescaleDB for time-series, graph databases for relationships, columnar stores for analytics). Each projection picks its store.

**Saga and long-running workflow orchestration.** Process managers handle multi-aggregate, multi-step business processes that don't fit single transactions. Lays the foundation for explicit workflow engines (Temporal, Camunda, Azure Durable Functions) if scale demands.

**API patterns: GraphQL, gRPC, REST.** With clean read models, GraphQL's resolver-per-field pattern maps to projections; gRPC services expose command and query surfaces; REST APIs serve task-based UIs (place order, cancel order) rather than CRUD shapes. All three coexist over the same event-sourced backend.

**Live dashboards and operational visibility.** WebSocket/SignalR/server-sent-events push updates to UIs as events flow. Operations teams see live system state (orders per minute, projection lag, queue depths, error rates) without polling.

**Multi-tenancy patterns.** Per-tenant event streams or tenant-scoped projections enable SaaS architectures. Tenant data isolation is enforceable at the event-stream level rather than at every query site.

**Cloud-native scaling.** Stateless command handlers scale horizontally; projections scale independently of writes; event stores can be cloud-managed (DynamoDB Streams, Kinesis, EventHub, KurrentDB cloud). Lays the foundation for serverless event processing (Lambda, Azure Functions on event triggers).

**Observability: structured logging, distributed tracing, correlation IDs.** Events naturally carry correlation IDs (what command caused this event), causation IDs (what event caused this follow-on event), and timestamps. Distributed tracing (OpenTelemetry, Jaeger) maps cleanly. Operations teams trace customer-facing failures end-to-end.

**Data lake and warehouse integration.** Event streams flow into data lakes (S3, Azure Data Lake) as immutable ground truth; warehouse loads (Snowflake, BigQuery, Redshift) consume the stream for analytical workloads. Eliminates the "extract from production database" coupling that breaks under load.

**ML/AI pipelines.** Event streams feed feature stores; historical events train predictive models (churn, fraud, recommendation, demand forecasting); model outputs become events themselves (PredictedRiskAssigned, FraudFlagged) that downstream consumers act on.

**Edge and offline-first.** Event-based replication works at the edge: mobile apps, branch offices, IoT devices can produce events locally and replicate when connected. The conflict-resolution story is clearer when the source of truth is "what happened" rather than "what is."

**Versioning and schema evolution.** Upcasting events from old to new shapes is a known, tractable pattern. The system handles schema change without big-bang migrations, which traditional shared databases struggle with under continuous deployment.

**Disaster recovery and geo-replication.** The event log is the only thing that must replicate; projections are derived and can rebuild at the destination. Cross-region replication, active-active topologies, and read-only failover regions all become more tractable than with mutable shared state.

**Compliance and data residency.** GDPR's right-to-be-forgotten interacts with event sourcing in well-understood ways (crypto-shredding, tombstone events, per-region storage). The patterns exist; they're documented; teams can implement them deliberately rather than discover the problem in production.

**Testing strategies.** Given-when-then aggregate tests, projection rebuild tests, and integration tests against the event log are all natural and well-tooled. Behavioral testing aligns with how the business describes the system. Brings BDD/DDD discipline within reach.
