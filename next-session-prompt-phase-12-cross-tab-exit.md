# Next session: Phase 12 exit condition, the cross-tab comparison (all four AdminConsole tools closed)

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by default and without being asked, because this reference implementation ships to readers for production use and runs in production environments. When options are available, the production-grade one is preferred. Re-derive from production-quality first principles rather than settling on exemplar parity, teaching clarity, cohesion-aesthetics, or convenience.

Repo state: HEAD is 3c3a230, the session-close doc 0048 commit, CI green and covering under the equality gate (run 29207637166, headSha equal to HEAD). This prompt's own commit advances HEAD past that, so the resume pre-flight reconciles the exact HEAD and CI from disk and bakes no value. Phase 12's four AdminConsole deliverables are all complete: the Projection Status Dashboard, the Replay Tool, the Event Store Browser, and the Correlation-ID Tracer. Each has a page, a seam, and a real-host characterization. Phase 12 stays open on its exit condition alone.

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
7. Counts and enumerations in a block are a starting point, not an authority. The executor reconciles them against disk and surfaces the difference. Two blocks this past session named a count that disk contradicted (three pm- literals where four exist, six commits where five do), and both were caught by the executor rather than by the block.

## START: resume pre-flight (read-only, neutral)

Produce one pasteable executor block that:

- reads CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full as its first action,
- prints HEAD, tree state, 0 ahead and 0 behind against origin/main, and the latest main CI conclusion, with no expected values baked in,
- applies the CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD; the ancestor, in-progress, and absent cases each stop and surface to the orchestrator loop,
- cross-checks HEAD against the newest session-close doc in docs/sessions/, identified from disk,
- grounds the two sides of the cross-tab comparison, which is the whole subject of the slice:
  - **The Web side.** The admin dashboard at /admin/throughput (src/Hosts/Web/Components/Pages/ThroughputDashboard.razor). What metric it shows, where the number comes from end to end: the query it dispatches, the read-side port behind it (IOrderThroughputStore at src/Domain/Sales/ReadModels/IOrderThroughputStore.cs, whose read path is GetBucketsAsync over per-second buckets), the read-model table those buckets live in, and the projection that fills it (OrderThroughputProjection). Report the metric's exact definition, its tenant scoping, and its time window.
  - **The AdminConsole side.** What each of the four tools shows, and which of them exposes anything commensurable with the Web meter. The Projection Status Dashboard at /projections reads ProjectionLagReader (src/Projections/Infrastructure/ProjectionLagReader.cs), which composes IEventStoreHeadPosition, ICheckpointStore, and the projection roster into head, checkpoint, and positions-behind per projection. The Event Store Browser, the Replay Tool, and the Correlation-ID Tracer each read the event store directly through their own focused seam.
  - **The substrate.** Where the two sides share a source and where they diverge. Both hosts point at one database in the compose stack; the Web meter reads a read-model table filled by a projection, and the AdminConsole reads the event store and the checkpoint table. State plainly whether any single number exists today that both sides render, or whether the comparison requires deriving one.

Report file:line first, verbatim where structure matters, and flag any drift from how a surface is named here. No edits, no staging, no commits.

## The opener: the Phase 12 exit condition

The opener is Phase 12's exit condition, the done-when inherited from Phase 11: the Web admin dashboard's metrics match what the AdminConsole tools show. All four tools now exist, so the condition is actionable for the first time. No comparison is wired.

Do not over-specify the design. The slice opens with the grounding pass above, and the design forks resolve in the orchestrator loop before any write.

**The first design question, first-order for the loop: what does "match" mean operationally, and where does the comparison live.** The grounding will likely show the two sides are not rendering the same number today. The Web meter shows order throughput per second from a read model; the AdminConsole shows projection lag, stream contents, and correlation traces from the event store. A match is therefore not a string equality between two rendered figures, and the loop has to decide what the criterion is before anything is built. Candidate framings to resolve, each with its own cost:

- **Consistency of a derived quantity.** For example, the order count the Web meter's buckets sum to over a window against the count of order-placed events the AdminConsole's substrate holds for that window and tenant. This is a real invariant and it is testable, but it requires deriving a number on the AdminConsole side that no tool renders today.
- **Freshness rather than value.** The comparison is that the Web meter is not stale, evidenced by the AdminConsole's projection lag for OrderThroughputProjection being zero or bounded. This uses what both sides already have and reads as an operator workflow, and it tests a weaker claim than value equality.
- **A new AdminConsole surface that renders the commensurable figure.** Honest, and it grows the console rather than only verifying it. Weigh against Phase 12's scope, which is four tools and no fifth.

**Where the comparison lives** is the second half of the same fork, and it is not settled: an automated cross-host test that drives both hosts and asserts the relation, a manual operator procedure recorded in a doc, or a single-host test that asserts the invariant on the shared substrate without booting two hosts. The Phase 11 done-when was written as a human cross-tab observation ("an order placed in one tab updates the customer dashboard in another"), which is worth reading before deciding whether the exit condition is a test or a procedure.

Resolve both halves in the loop before any write.

## Residual ledger

- EventStoreRepository.BuildFallbackMetadata survives on the aggregate write path (EventStoreRepository.cs:98 and :121-130), the structural sibling of the fallback that 0047 deleted from the process-manager repository. A null command context there still yields an empty correlation, causation, and actor with a Workers source. Whether the aggregate path should fail closed the same way is a candidate for its own commit.
- The null-context dead branches in TimeoutAwaitingPaymentForOrder and TimeoutAwaitingDispatchForOrder carry from 0047. They are unreachable in effect since a null context now throws at the save. Removal stays deferred to its own commit.
- The Browser's stream read is uncapped (StreamInspector.cs:47, fromVersion 0, no bound at any layer), which now sits asymmetric against the cap-bounded Tracer. Revisit if a hot stream makes the Browser's pull a real operator hazard.
- PostgresMigrationRunnerTests hardcodes the migration tail at 0021 (:163 and :317), so the next commit that adds a migration must touch that suite.
- The Replay Tool's seam has no integration coverage; the Browser's StreamInspectorTests is precedent rather than pattern.
- tests/PropertyTests holds only a .gitkeep and is absent from EventSourcingCqrs.slnx.
- The per-action RebuildProjection check stays deferred (ADR 0041 Revisit-when). The lag reader's seconds-behind and last-error still await substrate: read_models.projection_checkpoints carries projection_name and position with no timestamp or error column.
- Gate-findings debt (no scripted voice gate, the SQL false positives in the voice grep, the manifest-baseline convention) carries forward unchanged.

## Cross-track flags

Six flags stand, all for the doc-normalization commit and Phase 17. Code is canonical; the docs and manuscript normalize to it.

- The CLAUDE.md event-metadata field list and aggregate roster.
- The CLAUDE.md folder-layout block, stale across Domain, Infrastructure, projections, and test projects.
- CLAUDE.md and PLAN.md describing four event-store adapters as first-class peers while one production adapter exists; the SQL Server adapter scoped in Phase 2 never landed and carries no deferral note. This one needs a decision rather than a text edit.
- PLAN.md's Replay Tool description contradicting the shipped per-tenant page and ADR 0041's recorded rejection of whole-table truncate-and-replay.
- PLAN.md's Projection Status Dashboard text asserting the seconds-and-last-error substrate the ledger records as unbuilt.
- PLAN.md:423 describing the Correlation-ID Tracer's output as the chain of command, events, projection updates, and follow-on commands. The shipped tool is an event-row trace; projection updates are not in the event store at all. New at the 0048 close.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write, with named rejected alternatives and the load-bearing decisions stated before any write block. RED and GREEN are separate turns; show the RED failing for the intended reason, reported verbatim, before any production logic. Green-on-write guards are declared as such.
- A theater RED, where the production code already satisfies the behavior and a GREEN would write nothing, is committed as characterization with its provenance named, rather than presented as a RED.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision.
- Production quality over teaching clarity (ADR 0025), under the mandate at the top of this prompt.
- Commit lifecycle, in order: build clean under TreatWarningsAsErrors, named test run, full solution-wide dotnet test as the composition-drift gate (never per-project), pre-stage voice check, stage, voice grep, attribution grep (both on the staged added lines using the pipelines below), diff stat, commit, push as the explicit named step, CI read to completion before the next work stacks. CI in this repo is push-triggered; no run exists for an unpushed commit.
- **Pre-stage voice check, standing convention.** The voice pipeline runs over authored prose before staging, rather than only at the gate step after staging. Confirmed by the orchestrator-reviewer at the 0048 close after holding clean through every commit of that session. It catches a hit while the fix is still free.
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
- Flake ledger: (1) OrderPlacementEndToEndTests assertion race, re-run authorized. (2) Docker Hub registry pull timeout, re-run authorized; triage CI red by exception type first. (3) PostgresHubBackplaneConnectionTests and PostgresOrderListStore LISTEN OperationCanceledException, a delivery-wait timing flake. On a red CI run, capture the failure detail and report to the orchestrator loop; the re-run decision is made there, one authorized re-run per documented event.
- Voice gate, hard, on repo artifacts: no em-dashes, no ASCII double-hyphen as a dash, no filler intensifiers, no hedges, no corporate-fluff verbs, no "not X, Y" inversions, no AI attribution in commits, ADRs, docs, or session logs. Prefer neutral framing.
- Session-meta discipline: the close doc is committed and pushed before the session ends; scripts/manifest.sh against the last-synced baseline with the verify flag runs at the session-meta landing, the baseline read from git log; the planning workspace is hand-refreshed from the manifest output; a fresh next-session prompt is authored and committed at close.

## First move

Produce the START resume pre-flight block, grounded from disk. Bring the output back. Do not open the exit-condition slice or propose its design until the pre-flight reads true and both halves of the first design question, what a match means operationally and where the comparison lives, are resolved in the orchestrator loop.
