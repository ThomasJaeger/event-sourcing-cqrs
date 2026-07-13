# Next session: Phase 2's SQL Server event-store adapter, the abstraction's first stress test

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by default and without being asked, because this reference implementation ships to readers for production use and runs in production environments. When options are available, the production-grade one is preferred. Re-derive from production-quality first principles rather than settling on exemplar parity, teaching clarity, cohesion-aesthetics, or convenience.

Repo state: HEAD is f6ce3cb, the plan-amendment commit, CI green and covering under the equality gate (run 29265850518, headSha equal to HEAD). This prompt's own commit advances HEAD past that, so the resume pre-flight reconciles the exact HEAD and CI from disk and bakes no value.

## The amendment

This session completes Phase 2's SQL Server event-store adapter, ahead of Phase 13 (KurrentDB), per `docs/sessions/plan-amendment-sqlserver-adapter-ordering.md`. The ruling and its forcing facts live in that doc; read it before scoping.

The short of it: no SQL Server adapter exists on disk in any form, Phase 2 has been half-done for eleven phases with no deferral recorded anywhere, and both PLAN.md:579 and CLAUDE.md:260 designate this adapter as the abstraction's first stress test. `IEventStore` has never been exercised against a second engine. Opening Phase 13 first would make KurrentDB, which differs from PostgreSQL on several axes at once, the first proof of an abstraction that has never been tested against an engine differing on one.

`next-session-prompt-phase-13-kurrent-adapter.md` is superseded, not deleted. Phase 13 opens after this arc closes, re-grounded then.

## The version floor

Ruled in the orchestrator loop and binding on this session: **the minimum supported SQL Server version is 2019, and the primary target is 2022.**

The reasoning, each point checkable:

- SQL Server 2016 leaves extended support on 2026-07-14. A floor there would ship an adapter whose minimum is unsupported the day it lands.
- Official Linux container images start at 2017. The repo's test strategy is Testcontainers, and CI runs on ubuntu-latest, so any floor below 2017 cannot be proven in CI at all. A version the suite cannot exercise is a claim rather than a floor.
- 2017 runs out of extended support in October 2027 and has no UTF-8 collation support, which puts a real constraint on how JSON payloads are stored.
- 2019 introduces UTF-8 collations and carries extended support to January 2030. It is the oldest version that both survives the book's shelf life and can be proven in CI.
- 2022 is where new deployments land, so it is the primary target rather than the floor.

The floor is one named version, and it is named so that CI proves it rather than the docs asserting it. An adapter that claims to support a version no test runs against is exactly the kind of untested claim this phase exists to retire.

## Ordering note

Book-repo work waits on a complete reference implementation. The flag-ledger pass and the Phase 17 manuscript reconciliation sit behind code completion. The deferral is recorded here so Phase 17 opens knowing the ledger work is owed first.

## Who is who

The planner and orchestrator runs in the planning workspace and works with the human orchestrator-reviewer. The planner plans, proposes, resolves forks, and produces exact pasteable executor prompt blocks. The planner does not hold the repo and runs nothing.

The code executor has the source locally and reads and reasons over it. The human orchestrator-reviewer, Thomas, pastes the planner's blocks into the executor terminal, relays the output back, and makes the calls on the forks the planner surfaces. The planner never fabricates disk state or claims to have run a block.

The split:

- The planner: plan, propose with named rejected alternatives and explicit load-bearing decisions, resolve forks in the orchestrator loop, author the pasteable blocks.
- The human orchestrator-reviewer: runs the blocks, returns output, decides the forks.
- The code executor: reads the working-pattern docs first, inspects source, authors against verbatim on-disk signatures, runs gates, reports.

## How to drive the executor

1. Ask targeted questions rather than requesting whole-file dumps. Prefer file:line answers to whole-file reads.
2. Full-file reads only where authoring needs exact structure, kept inline-sized.
3. Every pasteable block is exact and self-contained. No orchestrator asides inside the block, no expected values baked into bash. Resolve decisions in the orchestrator loop first.
4. First action in every executor block: read CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full. Instruct it explicitly.
5. Path drift: the solution file is EventSourcingCqrs.slnx at the repo root. Name the on-disk forms in blocks.
6. Hand the executor the decisions and constraints and let it reason over the live tree. Do not over-specify line-level edits. The executor reads and reasons about the code better than the orchestrator can from summaries; give it the load-bearing decisions and the surfaces, and let it author against disk.
7. **Rule fix shapes as invariants, never as verbatim code.** The loop rules the property that must hold; the executor derives the edit against every reader and caller on disk. Recorded at the 0049 close, and paid for: a line-level ruled shape for the OutboxProcessor teardown named two fields and missed a third reader of one of them, so the repair landed incomplete and CI caught the moved race on the first push.
8. **Fix-forward on a self-inflicted red main is bounded.** Permitted only when the fix is bounded, locally proven, and the deviation is flagged in the same report that carries it. Otherwise revert first and return to the loop. Recorded at the 0049 close; precedent c9ec7c1.
9. Counts and enumerations in a block are a starting point, not an authority. The executor reconciles them against disk and surfaces the difference. This has caught a block's error in each of the last several sessions, including a ruled composition that disk proved could not produce data at all.

## START: resume pre-flight (read-only, neutral)

Produce one pasteable executor block that:

- reads CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full as its first action,
- prints HEAD, tree state, 0 ahead and 0 behind against origin/main, and the latest main CI conclusion, with no expected values baked in,
- applies the CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD; the ancestor, in-progress, and absent cases each stop and surface to the orchestrator loop,
- cross-checks HEAD against the newest session-close doc in docs/sessions/, identified from disk,
- reads `docs/sessions/plan-amendment-sqlserver-adapter-ordering.md`, PLAN.md's Phase 2 section, ADR 0004, and TDD_RULES.md §5, and reports what each requires of this adapter,
- grounds the following, reporting file:line and verbatim where structure matters:
  - **The `IEventStore` port surface.** Every method, its exact signature, and the semantics the Postgres adapter gives each: the concurrency-conflict contract, the global-position semantics, and how a non-existent stream reads. Then the ports beyond the aggregate lifecycle that the system has grown around it: `IEventStoreHeadPosition`, the correlation-trace reader, the delay queue, the idempotency store. Report which of these a second event-store adapter must also implement and which belong to PostgreSQL as a database rather than to the event store as a peer. That boundary is a design fork, not a fact; report the surfaces and let the loop rule it.
  - **The Postgres adapter's file inventory** as a shape reference, not a template. ADR 0004 forbids a shared relational layer, so the SQL Server adapter duplicates the structural pattern and shares no code. Report the file list with a one-line role each, so the loop can see what a self-contained second adapter has to carry.
  - **The contract tests.** TDD_RULES.md §5 requires one suite that all four adapters pass identically, and PLAN.md:607 makes it a v1 completion criterion. Report what exists: whether any shared or abstract suite is on disk, or whether every adapter test is Postgres-homed. Be precise about where each lives. This decides whether the arc opens by writing an adapter or by first extracting the suite the adapter is supposed to pass.
  - **The outbox and its notification mechanics.** What `OutboxProcessor` does, how LISTEN/NOTIFY wakes it, and what the migration-level trigger contributes. Then report PLAN.md:245 verbatim, which scopes SQL Server's projection trigger as polling in v1 and defers the engine-native alternatives (Service Broker, Change Tracking). The loop rules the SQL Server outbox shape with that coupling visible.
  - **Migrations.** How `migrations/` is laid out, how the runner discovers and applies them, and how the migration-tail assertions in PostgresMigrationRunnerTests are pinned. Report how a second engine's schema would fit that layout, since T-SQL and PostgreSQL DDL cannot share a file.
  - **The compose stack and Testcontainers packaging.** What services `docker/docker-compose.yml` defines and what Testcontainers packages `Directory.Packages.props` pins, against Phase 1's done-when at PLAN.md:209 and :225 that four services come up healthy.
  - **The SQL Server image and package, grounded rather than assumed.** The Testcontainers MsSql package, and which `mcr.microsoft.com/mssql/server` tags for 2019 and 2022 the environment can pull. Report what the environment proves it can reach, so the image pin the session commits to is a fact rather than a guess. The version floor above is ruled; the tags that satisfy it are a disk question.
  - **The configuration switch.** How the event store is selected today. Report the host call sites verbatim. CLAUDE.md:105 promises that switching is a configuration change rather than a code change; report what disk does today, because that promise is what this phase's done-when at PLAN.md:253 has to make true.

Report file:line first, verbatim where structure matters, and flag any drift from how a surface is named here. No edits, no staging, no commits.

## The opener: the design forks, resolved in the loop before any write

The pre-flight leads. These are the forks that are visible from here, named so the loop can rule them, and none is pre-decided.

- **The adapter's project shape under ADR 0004.** The ADR is explicit that each adapter owns its row construction, INSERT SQL, concurrency translation, and outbox mechanics inside its own project, with no shared relational layer and an accepted 30 to 50 lines of duplication. What that means concretely for the file set, and whether anything the Postgres project currently holds is a database concern rather than an event-store concern, is the first call.
- **The contract suite's sharing mechanism.** TDD_RULES.md §5 requires one identical suite across four adapters, and disk appears to have none. Extracting it is the honest reading of the rule and it is also the thing that makes the four-peer claim checkable. How it is parameterized, where it lives, and what it demands of a backend are open.
- **The outbox shape for SQL Server.** Polling in v1 per PLAN.md:245, with the engine-native triggers deferred. The Postgres processor is built around a LISTEN/NOTIFY wake with an idle-poll fallback, so the question is what the SQL Server processor keeps, what it drops, and whether the shared behavior is a contract the suite pins or a coincidence of two implementations.
- **Concurrency semantics mapping.** PLAN.md:235 names the engine-specific unique-violation codes, SQLState 23505 for PostgreSQL and error 2627 for SQL Server, each translated inside its own adapter into the one store-agnostic `ConcurrencyException`. The suite pins the contract; the adapters own the translation.
- **The payload and metadata column type.** The version floor opens this one. PLAN.md:236 states NVARCHAR(MAX). A 2019 floor makes VARCHAR(MAX) under a UTF-8 collation available, which roughly halves storage for the ASCII-dominant JSON this system writes, at the cost of diverging from the shape PLAN.md states and with non-ASCII payload behavior to weigh. Either way the PLAN divergence is flagged: keeping NVARCHAR(MAX) leaves the storage cost on the table, and taking VARCHAR(MAX) makes PLAN.md:236 stale. Ruled in the loop, pre-decided here neither way.
- **The CI matrix.** Whether the contract suite runs against the 2019 image alone, which is the floor and the thing most at risk of breaking, or against both 2019 and 2022. Decided against measured suite runtime once the suite exists, rather than in advance. A second engine in the matrix is real CI wall-clock, and the number is knowable rather than guessable.
- **What a leak looks like.** Both PLAN.md:579 and CLAUDE.md:260 instruct that awkwardness in this adapter is a signal about the abstraction rather than about the adapter. If `IEventStore` does not fit the second engine cleanly, that is the finding the phase exists to produce, and it surfaces to the loop rather than being worked around in the adapter.

Three candidate shapes travel with those forks as session inputs. Each is a starting point the loop rules on, not a decision, and the executor reconciles each against what the live engine does rather than against this list.

- Optimistic concurrency: a unique index on (stream_id, version), with errors 2627 and 2601 both translated to `ConcurrencyException`. The Postgres adapter translates one SQLSTATE; SQL Server can raise either number for a uniqueness violation depending on the constraint's shape, so the translation covers both.
- The correlation, causation, and tenant columns the Postgres schema derives with generated columns: persisted computed columns over `JSON_VALUE` are the SQL Server analogue, and whether they are indexed follows the read paths that exist.
- The polling claim, standing in for `FOR UPDATE SKIP LOCKED`: an UPDATE with READPAST, UPDLOCK, and ROWLOCK, using OUTPUT to return the claimed rows in the same statement.

## Residual ledger

Carried from the 0049 close.

- EventStoreRepository.BuildFallbackMetadata survives on the aggregate write path (EventStoreRepository.cs:98 and :121). A null command context there still yields an empty correlation, causation, and actor with a Workers source. Whether the aggregate path should fail closed the way the process-manager repository now does is a candidate for its own commit.
- The null-context dead branches in TimeoutAwaitingPaymentForOrder and TimeoutAwaitingDispatchForOrder carry from 0047, unreachable in effect since a null context throws at the save. Removal stays deferred.
- The Browser's stream read is uncapped (StreamInspector.cs:47, fromVersion 0, no bound at any layer), asymmetric against the cap-bounded Tracer.
- PostgresMigrationRunnerTests hardcodes the migration tail at 0021 (:163, :244, and :317), so the next commit that adds a migration touches that suite. This phase is likely to add one.
- The Replay Tool's seam has no integration coverage.
- tests/PropertyTests holds only a .gitkeep and is absent from EventSourcingCqrs.slnx.
- The per-action RebuildProjection check stays deferred (ADR 0041 Revisit-when). The lag reader's seconds-behind and last-error still await substrate: read_models.projection_checkpoints carries projection_name and position with no timestamp and no error column.
- DisposeListenerConnectionAsync keeps a check-then-act on _listenerConnection (OutboxProcessor.cs:165-178), reached by both entries of a re-entrant StopAsync. Benign today, since a doubled entry can only double-dispose an NpgsqlConnection, which is idempotent, inside a catch-and-log. Revisit if connection teardown grows a step that is not idempotent. A SQL Server processor written to the same shape inherits the question.
- The "empty handler set" clause at EventStore.Postgres/ServiceCollectionExtensions.cs:215-216 is stale: the Api host calls AddReadModels, which registers all six projections and their handler forwardings. The split's race rationale stands; that clause does not.
- The meter-semantics family, one entry. The orders-per-minute label counts eight Sales event types, several of which repeat within one order. The window anchors on the newest populated bucket, so a quiet tenant keeps rendering its last non-zero rate rather than decaying to zero. The bare IQueryBus overload reads throughput without the permission check. Retention pruning is write-triggered, so a quiet tenant's buckets never prune. OrderThroughputConsistencyTests pins the current window semantics, including the inclusive ends and the 61 second-buckets that follow, so a semantics change touches that test by design.
- Gate-findings debt carries forward unchanged.

## Cross-track flags

Seven flags stand, all for the doc-normalization commit and Phase 17. Code is canonical; the docs and manuscript normalize to it.

- The CLAUDE.md event-metadata field list and aggregate roster.
- The CLAUDE.md folder-layout block, stale across Domain, Infrastructure, projections, and test projects.
- CLAUDE.md and PLAN.md describing four event-store adapters as first-class peers while one production adapter exists. **Partially discharged by the plan amendment**: the scope call is made, four peers stand, and SQL Server is built next. The prose normalization across PLAN.md and CLAUDE.md still waits for Phase 17, and this phase's landing is what makes that prose true.
- PLAN.md's Replay Tool description contradicting the shipped per-tenant page and ADR 0041's recorded rejection of whole-table truncate-and-replay.
- PLAN.md's Projection Status Dashboard text asserting the seconds-and-last-error substrate the ledger records as unbuilt.
- PLAN.md:423 describing the Correlation-ID Tracer's output as the chain of command, events, projection updates, and follow-on commands. The shipped tool is an event-row trace; projection updates are not in the event store at all.
- PLAN.md's done-when wording at :387, carried at :415, :417, and :437, predating ADR 0039 and reading as like-for-like metric parity between the Web dashboard and the AdminConsole tools. It normalizes to the substrate-consistency reading recorded in the db73dcc commit body and the OrderThroughputConsistencyTests header.

One candidate flag is expected to join the family at this session's close, and the close doc records it. The version floor is a code decision, and whether the manuscript states a minimum SQL Server version in print, or states the constraint by capability instead (JSON functions, UTF-8 collation), is a manuscript question this session cannot settle from the code side. It belongs to Phase 17 with the rest of the family.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write, with named rejected alternatives and the load-bearing decisions stated before any write block. RED and GREEN are separate turns; show the RED failing for the intended reason, reported verbatim, before any production logic. Green-on-write guards are declared as such.
- A theater RED, where the production code already satisfies the behavior and a GREEN would write nothing, is committed as characterization with its provenance named, rather than presented as a RED.
- Adapter internals are spike-then-stabilize per TDD_RULES.md §1: spike against the live backend to learn its real contract (Microsoft.Data.SqlClient exceptions, error numbers for concurrency), then make the adapter pass the shared contract suite. The spike is throwaway and is not the deliverable.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision. In this phase that carries extra weight: an `IEventStore` that does not fit the second engine is the finding the phase exists to produce.
- Fix shapes are ruled as invariants, not as verbatim code. Fix-forward over a red main is bounded. Both recorded at the 0049 close.
- Production quality over teaching clarity (ADR 0025), under the mandate at the top of this prompt.
- Commit lifecycle, in order: build clean under TreatWarningsAsErrors, named test run, full solution-wide dotnet test as the composition-drift gate (never per-project), pre-stage voice check, stage, voice grep, attribution grep (both on the staged added lines using the pipelines below), diff stat, commit, push as the explicit named step, CI read to completion before the next work stacks. CI in this repo is push-triggered; no run exists for an unpushed commit.
- **Pre-stage voice check, standing convention.** The voice pipeline runs over authored prose before staging, rather than only at the gate step after staging. It catches a hit while the fix is still free, and it caught one in each of the last two sessions.
- CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD. The ancestor case, the in-progress case, and the absent case each stop and surface to the orchestrator loop rather than passing.
- Voice grep, exact pipeline:

```
git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"
```

- Attribution grep, exact pipeline:

```
git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -niE "Co-Authored-By|Generated with|Claude|Anthropic"
```

- Gate-hit handling: a hit whose remedy the loop has already ruled (same token class, same convention) is fixed, re-gated, and reported with the ruling cited; any novel hit halts and returns to the loop. Known false-positive classes: SQL double-hyphen comments, which this phase will produce in volume as T-SQL lands; case-insensitive CLAUDE.md filename references in the attribution grep; and verbatim quotations of the canonical gate pipelines themselves, in working-pattern docs, session prompts, and close docs, where the exemption covers the quoted pipeline lines only and never the surrounding prose.
- Flake ledger: (1) OrderPlacementEndToEndTests assertion race, re-run authorized. (2) Docker Hub registry pull timeout, re-run authorized; triage CI red by exception type first. (3) PostgresHubBackplaneConnectionTests and PostgresOrderListStore LISTEN OperationCanceledException, a delivery-wait timing flake. On a red CI run, capture the failure detail and report to the orchestrator loop; the re-run decision is made there, one authorized re-run per documented event. A red that matches no ledger entry is a finding, not a flake: the last session's red CI was a real defect the local runs had hidden. A new engine in CI is a new flake surface; a first SQL Server container failure is triaged, not assumed.
- Voice gate, hard, on repo artifacts: no em-dashes, no ASCII double-hyphen as a dash, no filler intensifiers, no hedges, no corporate-fluff verbs, no "not X, Y" inversions, no AI attribution in commits, ADRs, docs, or session logs. Prefer neutral framing.
- Session-meta discipline: the close doc is committed and pushed before the session ends; scripts/manifest.sh against the last-synced baseline with the verify flag runs at the session-meta landing, the baseline read from git log; the planning workspace is hand-refreshed from the manifest output; a fresh next-session prompt is authored and committed at close.

## First move

Produce the START resume pre-flight block, grounded from disk. Bring the output back. Do not scope the adapter, propose a project shape, or write anything until the pre-flight reads true and the opening forks are resolved in the orchestrator loop.
