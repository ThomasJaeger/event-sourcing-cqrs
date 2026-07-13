# Next session: the adapter arc opens (Phase 13, KurrentDB), with the adapter-peers scope call first

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by default and without being asked, because this reference implementation ships to readers for production use and runs in production environments. When options are available, the production-grade one is preferred. Re-derive from production-quality first principles rather than settling on exemplar parity, teaching clarity, cohesion-aesthetics, or convenience.

Repo state: HEAD is 0edfc12, the session-close doc 0049 commit, CI green and covering under the equality gate (run 29263482773, headSha equal to HEAD). This prompt's own commit advances HEAD past that, so the resume pre-flight reconciles the exact HEAD and CI from disk and bakes no value. Phase 12 is closed: all four AdminConsole tools shipped, and the exit condition is discharged as substrate-consistency and pinned by OrderThroughputConsistencyTests.

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
7. **Rule fix shapes as invariants, never as verbatim code.** The loop rules the property that must hold; the executor derives the edit against every reader and caller on disk. New at the 0049 close, and it was paid for: a line-level ruled shape for the OutboxProcessor teardown named two fields and missed a third reader of one of them, the repair landed incomplete, and CI caught the moved race on the first push.
8. Counts and enumerations in a block are a starting point, not an authority. The executor reconciles them against disk and surfaces the difference. This has caught a block's error in each of the last several sessions, including a ruled composition that disk proved could not produce data at all.

## START: resume pre-flight (read-only, neutral)

Produce one pasteable executor block that:

- reads CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full as its first action,
- prints HEAD, tree state, 0 ahead and 0 behind against origin/main, and the latest main CI conclusion, with no expected values baked in,
- applies the CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD; the ancestor, in-progress, and absent cases each stop and surface to the orchestrator loop,
- cross-checks HEAD against the newest session-close doc in docs/sessions/, identified from disk,
- grounds what Phase 13 needs, as docs/PLAN.md defines Phase 13 rather than as this prompt summarizes it. Read that section first and scope the grounding from it. At minimum:
  - **The on-disk state of the adapter projects.** Report what src/Infrastructure/EventStore.Kurrent and src/Infrastructure/EventStore.DynamoDb contain, whether an EventStore.SqlServer directory exists at all, and which of these are in EventSourcingCqrs.slnx. Verify rather than assume; the last grounding pass found both adapter directories carrying zero .cs files and no SQL Server directory on disk, and that is the fact the opener turns on.
  - **The IEventStore contract as the Postgres adapter has shaped it.** The port and every method on it, the concurrency-conflict contract, the global-position semantics, and what the outbox and the head-position port add beyond the aggregate lifecycle. The KurrentDB adapter is the first real test of whether the abstraction is real or aspirational, so the pre-flight states what a second adapter would have to satisfy.
  - **The shared contract suite, if one exists.** docs/TDD_RULES.md §5 specifies one suite that all four adapters pass. Report whether that suite exists on disk today, what it covers, and whether the Postgres adapter is tested through it or through adapter-specific tests. This decides whether Phase 13 opens by writing an adapter or by first extracting the suite the adapter is supposed to pass.
  - **The projection trigger seam.** Phase 13 replaces polling with native catch-up subscriptions when KurrentDB is the store. Report how projections are driven today, and be precise: the steady-state path is push through the outbox, not the pull the architecture docs describe.

Report file:line first, verbatim where structure matters, and flag any drift from how a surface is named here. No edits, no staging, no commits.

## The opener: the adapter-peers scope call, before any write

The first-order question is not how to build the KurrentDB adapter. It is whether the four-peer commitment still stands.

CLAUDE.md and PLAN.md both describe four event-store implementations as first-class peers behind a common abstraction: PostgreSQL, SQL Server, KurrentDB, and DynamoDB. On disk there is one production adapter. The SQL Server adapter was scoped in Phase 2, never landed, and carries no deferral note anywhere. The KurrentDB and DynamoDB directories exist and are empty. This is the one cross-track flag in the family that needs a scope decision rather than a text edit, and it has been carried for several closes.

The arc should not open until the loop decides what it is opening. The fork, stated plainly:

- **Hold the four-peer commitment.** Phase 13 and Phase 14 build KurrentDB and DynamoDB, and SQL Server gets a phase or an explicit deferral note. This is the largest scope and the strongest claim the book can make about the abstraction being real.
- **Reduce the commitment and normalize the docs.** Fewer peers, with the rest named as discussed alternatives the way Marten already is. Cheapest, and it costs the book a claim it currently makes in two places.
- **Something between.** For example, one more adapter as the proof that the abstraction is real, and the remainder documented as deliberate non-goals.

Whichever way it goes, CLAUDE.md and PLAN.md are edited to match, because the docs currently assert a shape the code does not have. Resolve this in the loop before the first write, and derive the phase's scope from the answer rather than from PLAN.md's current text.

## Phase 13 as PLAN.md currently scopes it

Quoted here because the opener may change it. PLAN.md's Phase 13 goals: the EventStore.Kurrent adapter implementing IEventStore against KurrentDB via the gRPC client; append, read, and optimistic concurrency mapped to KurrentDB semantics; a configuration switch so the same domain code runs on a different event store with no code changes outside the infrastructure layer; a native catch-up subscription mechanism for projections, replacing polling when KurrentDB is the configured store; integration tests against KurrentDB via Testcontainers; and documentation of trade-offs in code comments and an ADR. Out of scope: KurrentDB-specific features beyond what the abstraction needs. Done when: all existing aggregate, projection, and process-manager tests pass with the configuration switched to KurrentDB; native subscriptions feed projections without polling; and the Event Store Browser works against KurrentDB.

Note the third done-when item against what Phase 12 shipped. The Event Store Browser reads through IStreamInspector over IEventStore, so the claim is testable, and the pre-flight should confirm the seam is store-agnostic rather than assume it.

## Residual ledger

- EventStoreRepository.BuildFallbackMetadata survives on the aggregate write path (EventStoreRepository.cs:98 and :121). A null command context there still yields an empty correlation, causation, and actor with a Workers source. Whether the aggregate path should fail closed the way the process-manager repository now does is a candidate for its own commit.
- The null-context dead branches in TimeoutAwaitingPaymentForOrder and TimeoutAwaitingDispatchForOrder carry from 0047, unreachable in effect since a null context throws at the save. Removal stays deferred.
- The Browser's stream read is uncapped (StreamInspector.cs:47, fromVersion 0, no bound at any layer), asymmetric against the cap-bounded Tracer.
- PostgresMigrationRunnerTests hardcodes the migration tail at 0021 (:163, :244, and :317), so the next commit that adds a migration touches that suite.
- The Replay Tool's seam has no integration coverage.
- tests/PropertyTests holds only a .gitkeep and is absent from EventSourcingCqrs.slnx.
- The per-action RebuildProjection check stays deferred (ADR 0041 Revisit-when). The lag reader's seconds-behind and last-error still await substrate: read_models.projection_checkpoints carries projection_name and position with no timestamp and no error column.
- DisposeListenerConnectionAsync keeps a check-then-act on _listenerConnection (OutboxProcessor.cs:165-178), reached by both entries of a re-entrant StopAsync. Benign today, since a doubled entry can only double-dispose an NpgsqlConnection, which is idempotent, inside a catch-and-log. Revisit if connection teardown grows a step that is not idempotent. New at the 0049 close.
- The "empty handler set" clause at EventStore.Postgres/ServiceCollectionExtensions.cs:215-216 is stale: the Api host calls AddReadModels, which registers all six projections and their handler forwardings. The split's race rationale stands; that clause does not. New at the 0049 close.
- The meter-semantics family, one entry. The orders-per-minute label counts eight Sales event types, several of which repeat within one order. The window anchors on the newest populated bucket, so a quiet tenant keeps rendering its last non-zero rate rather than decaying to zero. The bare IQueryBus overload reads throughput without the permission check. Retention pruning is write-triggered, so a quiet tenant's buckets never prune. OrderThroughputConsistencyTests pins the current window semantics, including the inclusive ends and the 61 second-buckets that follow, so a semantics change touches that test by design. New at the 0049 close.
- Gate-findings debt carries forward unchanged.

## Cross-track flags

Seven flags stand, all for the doc-normalization commit and Phase 17. Code is canonical; the docs and manuscript normalize to it.

- The CLAUDE.md event-metadata field list and aggregate roster.
- The CLAUDE.md folder-layout block, stale across Domain, Infrastructure, projections, and test projects.
- CLAUDE.md and PLAN.md describing four event-store adapters as first-class peers while one production adapter exists; the SQL Server adapter scoped in Phase 2 never landed and carries no deferral note. This one needs a decision rather than a text edit, and it is this session's opener.
- PLAN.md's Replay Tool description contradicting the shipped per-tenant page and ADR 0041's recorded rejection of whole-table truncate-and-replay.
- PLAN.md's Projection Status Dashboard text asserting the seconds-and-last-error substrate the ledger records as unbuilt.
- PLAN.md:423 describing the Correlation-ID Tracer's output as the chain of command, events, projection updates, and follow-on commands. The shipped tool is an event-row trace; projection updates are not in the event store at all.
- PLAN.md's done-when wording at :387, carried at :415, :417, and :437, predating ADR 0039 and reading as like-for-like metric parity between the Web dashboard and the AdminConsole tools. It normalizes to the substrate-consistency reading recorded in the db73dcc commit body and the OrderThroughputConsistencyTests header. New at the 0049 close.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write, with named rejected alternatives and the load-bearing decisions stated before any write block. RED and GREEN are separate turns; show the RED failing for the intended reason, reported verbatim, before any production logic. Green-on-write guards are declared as such.
- A theater RED, where the production code already satisfies the behavior and a GREEN would write nothing, is committed as characterization with its provenance named, rather than presented as a RED.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision.
- **Fix shapes are ruled as invariants, not as verbatim code.** The loop rules the property; the executor derives the edit against every reader on disk. New at the 0049 close.
- **Fix-forward on a self-inflicted red main is bounded.** It is permitted only when the fix is bounded, locally proven, and the deviation is flagged in the same report that carries it. Otherwise revert first and return to the loop. The standing CI rule and the stop-and-ask rule pull against each other here, and this is where fixing forward wins. Precedent: c9ec7c1. New at the 0049 close.
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

- Gate-hit handling: a hit whose remedy the loop has already ruled (same token class, same convention) is fixed, re-gated, and reported with the ruling cited; any novel hit halts and returns to the loop. Known false-positive classes: SQL double-hyphen comments; case-insensitive CLAUDE.md filename references in the attribution grep; and verbatim quotations of the canonical gate pipelines themselves, in working-pattern docs, session prompts, and close docs, where the exemption covers the quoted pipeline lines only and never the surrounding prose.
- Flake ledger: (1) OrderPlacementEndToEndTests assertion race, re-run authorized. (2) Docker Hub registry pull timeout, re-run authorized; triage CI red by exception type first. (3) PostgresHubBackplaneConnectionTests and PostgresOrderListStore LISTEN OperationCanceledException, a delivery-wait timing flake. On a red CI run, capture the failure detail and report to the orchestrator loop; the re-run decision is made there, one authorized re-run per documented event. A red that matches no ledger entry is a finding, not a flake: the last session's red CI was a real defect the local runs had hidden.
- Voice gate, hard, on repo artifacts: no em-dashes, no ASCII double-hyphen as a dash, no filler intensifiers, no hedges, no corporate-fluff verbs, no "not X, Y" inversions, no AI attribution in commits, ADRs, docs, or session logs. Prefer neutral framing.
- Session-meta discipline: the close doc is committed and pushed before the session ends; scripts/manifest.sh against the last-synced baseline with the verify flag runs at the session-meta landing, the baseline read from git log; the planning workspace is hand-refreshed from the manifest output; a fresh next-session prompt is authored and committed at close.

## First move

Produce the START resume pre-flight block, grounded from disk. Bring the output back. Do not open the adapter arc, scope Phase 13, or propose any design until the pre-flight reads true and the adapter-peers scope call is resolved in the orchestrator loop.
