# Next session: Phase 2 slice 6, the provider switch, and the phase close

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by
default and without being asked, because this reference implementation ships to
readers for production use and runs in production environments. Re-derive from
production-quality first principles rather than settling on exemplar parity,
teaching clarity, cohesion-aesthetics, or convenience.

## Session scope, ruled in the orchestrator loop at the 0050 close

1. The provider switch and the connection-string split (one code commit).
2. The PLAN.md:253 done-when test, in the same commit.
3. The doc commit: ADR 0045 (text below) and the ADR 0044 amendment.
4. One rider: the deterministic idempotency race fact backfilled to PostgreSQL
   as a characterization with provenance declared. (The retry-policy rider was
   discharged at fe0cd43.)
5. The formal Phase 2 close: a done-when reconciliation across PLAN.md:247-253
   recorded in the session close doc. PLAN.md itself is not edited; its prose
   normalization stays with Phase 17.

Read docs/sessions/0050-phase2-sqlserver-arc-close.md for the full ledger,
flags, and the arc's history. Disk is authoritative over this prompt.

## Binding rulings

**The switch.** An EventStore:Provider configuration value read at each host
root that composes IEventStore. Recognized values Postgres (default when
absent, preserving current behavior exactly) and SqlServer; any other value
fails loudly at startup with the offending value in the message. Each branch
calls its adapter's registration extensions, mirroring today's placement per
host (the 0050-era call sites, as starting points to reconcile: Api/Program.cs
:57 event store only; Workers/WorkersHostFactory.cs:46, :52, :56 event store
plus outbox processor plus delay-queue processor). AdminConsole stays
PostgreSQL-bound this arc, recorded as a residual. Web composes no event store
and is untouched.

**The connection-string split.** The selected provider's connection string
names the database holding events, outbox, command idempotency, and delayed
commands. The composing host runs the selected provider's migration runner
against it and never the other engine's (Workers/Program.cs:34-43 is the
Postgres-runner site to branch). The read-model database stays PostgreSQL under
its own key. A provider and connection mismatch fails loudly at startup where
detectable. Config shape is derived against how strings reach hosts today
(environment variables; no appsettings files exist).

**The :253 test.** In IntegrationTests: overlay the Api host with
EventStore:Provider=SqlServer and a SQL Server connection string, boot it,
drive one real command through the API surface, assert the event row exists in
the SQL Server events table by raw read. Scope the assertion to the append if
projection flow would hang on the read-model split, stating the scoping. If
grounding refutes this composition, STOP and surface with the refuting facts;
the slice 4 precedent is the model.

**The doc commit** lands after the code commit, on its own lifecycle. ADR 0045
text is confirmed in the loop and follows; reconcile cites against disk and
report adjustments. The ADR 0044 amendment: a short SQL Server paragraph in the
ADR's voice stating the hazard is latent under lock-based READ COMMITTED and
active under READ_COMMITTED_SNAPSHOT, sp_getapplock at transaction scope holds
the invariant on both postures, fixtures enable RCSI so the probe tests the
skip rather than the blocking, and ADR 0045 carries the full mapping.

## ADR 0045, confirmed text

File: docs/adr/0045-sqlserver-adapter-engine-mappings.md (verify the number is
free).

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
therefore carries none, ever, and the outbox is polling-only per PLAN.md:245.
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

**Configuration.** One connection-string key had carried three roles: the
event store's database, the database the PostgreSQL migration runner migrates,
and the database holding the companion tables. Provider selection resolves the
overload: the selected provider's connection string names the database holding
events, outbox, command idempotency, and delayed commands; the composing host
runs the selected provider's migration runner against it and never the other
engine's; the read-model database stays PostgreSQL under its own key. A
provider and connection mismatch fails loudly at startup where detectable.

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
single image already costs roughly 90 seconds of container tests per run.

## Consequences

- The switching guarantee at PLAN.md:253 is made true by the host-level
  EventStore:Provider selection, defaulting to Postgres and failing loudly on
  unknown values.
- The message-substring translation filter is a weaker contract than a
  structured field; a constraint rename breaks translation, which the contract
  suite's concurrency facts would catch.
- PLAN.md:235 (error 2627) and :236 (NVARCHAR(MAX)) are each incomplete or
  diverged against these decisions; both carry cross-track flags for Phase 17.

## Trigger for revisiting

Version-conditional adapter behavior reopens the CI matrix. A third relational
adapter reopens ADR 0004's factoring question with the corrected cost figure.
An engine-native outbox wake (Service Broker, Change Tracking) remains
deferred per PLAN.md:245 and would be its own ADR.

## START: resume pre-flight (read-only)

One pasteable block that: reads the working-pattern docs in full first; prints
HEAD, tree state, ahead/behind, and applies the CI equality gate with no baked
values; cross-checks the newest close doc (0050); then grounds, file:line
first and verbatim where structure matters: every host event-store call site;
the Workers migration-runner block; how configuration reaches each host;
ApiFixture's connection-string feeds; both adapters' registration extension
surfaces, confirming branch symmetry; IntegrationTests fixture conventions for
a SQL Server container; and AdminConsole's focused extensions. Counts and
line numbers in this prompt are starting points; reconcile and report drift.
Do not open the write work until the pre-flight reads true in the loop.

## Working-pattern rules (hold all)

Disk over docs over this prompt. Propose before write with named rejected
alternatives. RED and GREEN separate turns, RED verbatim; characterizations
declared with provenance. Fix shapes ruled as invariants; the executor derives
edits against every reader on disk. Fix-forward on a self-inflicted red main
only when bounded, locally proven, and flagged in the carrying report; a ruled
STOP whose resolution the ruling already implies may be carried through
bounded and flagged (0050). Full commit lifecycle in order: build clean under
TreatWarningsAsErrors, named test run, solution-wide dotnet test, pre-stage
voice check, stage, voice grep, attribution grep, diff stat, commit, push as
its own step, CI read to completion under the equality gate (headSha equals
HEAD exactly; ancestor-green never clears). Flake ledger per 0049/0050; a SQL
Server container failure is a finding, never an assumed flake. Voice-gate
false-positive classes, four: T-SQL comment tokens; case-insensitive
working-pattern filename references in the attribution grep; verbatim
quotations of the gate pipelines themselves; XML comment delimiters in project
files (delimiters only, never the prose). Session-meta md-only commits may
share one solution-wide gate run, named in the report.

Voice grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"

Attribution grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -niE "Co-Authored-By|Generated with|Claude|Anthropic"

## After the phase closes

Phase 13 (KurrentDB) reopens on its own pre-flight against whatever this arc
leaves on disk, inheriting the contract suite, ADR 0044's ordering contract,
and the open companion-port boundary for non-relational engines. The
book-repo flag-ledger pass remains owed ahead of Phase 17.
