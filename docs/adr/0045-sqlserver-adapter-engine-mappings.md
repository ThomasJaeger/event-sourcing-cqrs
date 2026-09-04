# 0045. SQL Server Adapter Engine Mappings

## Status

Accepted (July 2026)

## Context

Phase 2's SQL Server adapter is the first second-engine implementation of
IEventStore (ADR 0004 self-containment; ADR 0044 ordering contract). A
throwaway spike against SQL Server 2019 (TDD_RULES section 1) established the
engine's real contract before authoring. Several mappings differ from the
PostgreSQL adapter in ways that are silent hazards rather than compile errors;
this ADR records them and the decisions they forced. The version floor is SQL
Server 2019, primary target 2022, chosen so the floor survives the book's
shelf life and is provable in CI.

## Decisions

**Commit-ordered visibility (ADR 0044).** The hazard is
configuration-dependent on this engine: latent under default lock-based READ
COMMITTED, where a tailing reader blocks on the writer's lock, and active
under READ_COMMITTED_SNAPSHOT, where the reader skips the uncommitted row
exactly as PostgreSQL's MVCC does. RCSI is the Azure SQL default and a common
on-premises posture, so the adapter holds the invariant unconditionally with
sp_getapplock, exclusive, at transaction scope, as the first statement of both
append transactions. The test fixture enables RCSI on every test database,
because only under RCSI does the commit-visibility probe exercise the skip
hazard rather than the blocking behavior; an adapter tested only on a default
container would look safe and lose events in production.

**Concurrency translation.** Stream-version and event-id uniqueness ship as
named unique constraints (uq_events_stream_version, uq_events_event_id), with
auto-generated names forbidden. Both raise error 2627; a unique index would
raise 2601. SqlException carries no structured constraint-name field, so the
translator filters on the primary error's number in {2627, 2601} and a message
substring naming the constraint; 2601 is covered defensively against a future
index-shaped uniqueness. A duplicate event id propagates untranslated,
matching the contract suite's pin.

**JSON storage and binding.** Payload and metadata columns are VARCHAR(MAX)
under an explicit column-level UTF-8 collation
(Latin1_General_100_CI_AS_SC_UTF8), roughly halving storage against NVARCHAR
for this system's ASCII-dominant JSON. The parameters bind as
SqlDbType.NVarChar: binding VarChar converts client-side to the connection
codepage and silently destroys non-ASCII before it reaches the server, while
NVarChar lets the server convert into UTF-8. Identifier parameters bind
VarChar to preserve index seeks against VARCHAR columns. Both adapters pin
their serializer Encoder explicitly to the default ASCII-escaping encoder,
making the on-the-wire escaping a stated decision; the byte-level tripwire for
the binding lives as an adapter test, because the engine-agnostic suite's
payloads are ASCII after escaping and cannot detect the corruption.

**Position readback and triggers.** The append reads global_position back with
INSERT OUTPUT, which fails on tables carrying triggers. The events table
therefore carries none, ever, and the outbox is polling-only per the v1 scope decision that leaves engine-native SQL Server triggers unused.
The notification-free processors have no listener connection, no re-entrant
teardown surface, and no counterpart to the PostgreSQL processor's teardown
repair: a strict reduction in bug surface.

**Companion ports.** IIdempotencyStore and IDelayQueue are dependencies of
every complete host composition, because the idempotency pipeline behavior is
registered unconditionally for every command, and they are database concerns
rather than members of the event-store port. Each relational adapter ships
companion implementations against its own database; the SQL Server adapter
ships SqlServerIdempotencyStore, SqlServerDelayQueue, and a polling
SqlServerDelayQueueProcessor, and IEventStore gains no methods. What KurrentDB
and DynamoDB supply for these ports stays open for their arcs; ADR 0017
already anticipates per-adapter delay mechanisms. Companion processors build
their retry policies from their configured options through factory
registrations; a bare registration lets constructor defaults win silently, the
defect repaired at fe0cd43.

**Configuration.** One connection-string key had carried five roles: the
event store's database, the PostgreSQL migration runner's primary target, the
companion tables' database, AdminConsole's three read-side ports, and the
migration CLI's target. Provider selection, read from the flat
EVENT_STORE_PROVIDER environment key (Postgres when absent, preserving prior
behavior exactly; any unrecognized value fails at startup with the offending
value named), resolves the overload per database rather than per host: the
selected provider's connection string names the database holding events,
outbox, command idempotency, and delayed commands, and the read-model database
stays PostgreSQL under its own key. Workers, the sole migrating host, runs the
selected provider's runner against the event-store database and always the
PostgreSQL runner against the read-model database, whose read_models DDL
exists only in the PostgreSQL migration set. AdminConsole's read-side ports
have no SQL Server peers this arc, so it guards the provider key at startup
and throws on any non-Postgres value rather than booting against a string its
Npgsql-bound ports fail on at first use. The migration CLI stays
provider-unaware and PostgreSQL-only this arc, a recorded residual; pointed at
a SQL Server deployment it fails at connection rather than silently. Mismatch
detection is asymmetric: SqlClient rejects a PostgreSQL-shaped string at
parse, while Npgsql aliases Server and User Id and can accept a SQL
Server-shaped one; the startup guard is loud where the driver lets it be.

**Migrations.** The adapter owns migrations/sqlserver/ with its own runner:
embedded resources, GO-split batches (CREATE SCHEMA must sit alone in its
batch), serialized by sp_getapplock, tracked in its own schema_migrations
table. The PostgreSQL glob is non-recursive, so the two engines' migration
sets cannot cross-contaminate, verified from both assemblies' resource lists.

**ADR 0004 cost correction.** ADR 0004 estimated 30 to 50 duplicated lines
across the relational adapters. The real figure is roughly 234, because the
estimate counted row construction and translation and omitted the type
registries and JSON seam the second adapter also duplicates. The decision
holds at two adapters; the registries' own comments name
Infrastructure/Versioning in Phase 15 as the collapse point, and ADR 0004's
revisit trigger remains three adapters touching identical code.

**CI matrix.** The suite runs against the 2019 image only: the floor is what
CI must prove, no version-conditional code exists in the adapter, and the
single image already costs roughly 90 seconds of container tests per
Infrastructure.Tests run at the time of writing; the provider-switch test adds one
SQL Server container start to IntegrationTests.

## Consequences

- The switching guarantee in this repository's scope is made true for the hosts that
  compose IEventStore for writes (Api, Workers) by EVENT_STORE_PROVIDER
  selection, defaulting to Postgres and failing loudly on unknown values.
  AdminConsole reads the key only to refuse non-Postgres values until its
  read-side ports gain second-engine implementations.
- The PostgreSQL runner applies its full migration set to whatever database it
  is handed, so the split read-model database carries an inert event_store
  schema; accepted over splitting a migration sequence that the runner tests
  pin as a single 21-file set.
- Provider selection is a deployment-time choice for a fresh database set, not
  a runtime swap on populated data: checkpoints in the read-model database
  record positions from the selected engine's position source.
- The message-substring translation filter is a weaker contract than a
  structured field; a constraint rename breaks translation, which the contract
  suite's concurrency facts would catch.
- The v1 concurrency mapping (error 2627) and serialization choice (NVARCHAR(MAX)) are each incomplete or
  diverged against these decisions; both carry cross-track flags for Phase 17.

## Trigger for revisiting

Version-conditional adapter behavior reopens the CI matrix. A third relational
adapter reopens ADR 0004's factoring question with the corrected cost figure.
An engine-native outbox wake (Service Broker, Change Tracking) remains
deferred per the v1 scope decision that leaves engine-native SQL Server triggers unused and would be its own ADR.
