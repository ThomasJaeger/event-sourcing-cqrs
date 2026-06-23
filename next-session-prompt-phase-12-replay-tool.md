# Next session: Phase 12 Replay Tool (AdminConsole gate and Projection Status Dashboard closed)

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by default and without being asked, because this reference implementation ships to readers for production use and runs in production environments. When options are available, the production-grade one is preferred. Re-derive from production-quality first principles rather than settling on exemplar parity, teaching clarity, cohesion-aesthetics, or convenience.

Repo state: HEAD is 87c76dc, the session-close doc 0044 commit, CI green and covering. This prompt's own commit and the manifest refresh advance HEAD past that, so the resume pre-flight reconciles the exact HEAD and CI from disk and bakes no value. Phase 12 is active. The AdminConsole authorization gate is complete: the deny path proven test-first, the admit path characterized. The Projection Status Dashboard is complete, the first of Phase 12's four AdminConsole deliverables.

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
6. Hand the executor the decisions and constraints and let it reason over the live tree. Do not over-specify line-level edits. The executor reads and reasons about the code better than the orchestrator can from summaries; give it the load-bearing decisions and the surfaces, and let it author against disk. This was a mid-session correction and it earns its place here.

## START: resume pre-flight (read-only, neutral)

Produce one pasteable executor block that:

- reads CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full as its first action,
- prints HEAD, tree state, 0 ahead and 0 behind against origin/main, and the latest main CI conclusion, with no expected values baked in,
- applies the CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD; the ancestor, in-progress, and absent cases each stop and surface to the orchestrator loop,
- confirms ADR 0040 is on disk and the Projection Status Dashboard is complete: the /projections page renders behind the host fallback gate, composed from the lag reader's three focused registrations,
- grounds the Replay Tool's starting surfaces:
  - the live TruncateAsync no-op: PostgresOrderThroughputStore.TruncateAsync at src/Infrastructure/ReadModels.Postgres/PostgresOrderThroughputStore.cs:77, the named Phase 12 residual this slice promotes from no-op to real, with the sibling methods GetBucketsAsync, IncrementSecondAsync, and PruneBeforeAsync live,
  - how projections rebuild today: the catch-up path, ProjectionStartupCatchUpService and ProjectionReplayer in Projections.Infrastructure, which a replay must drive,
  - the checkpoint advance and read surface: ICheckpointStore in Domain.Abstractions exposes AdvanceAsync and GetPositionAsync and has no reset method, so a replay's checkpoint reset goes through a separate surface, the RebuildModeCheckpointStore wrapper and the ITenantResettable read-model row reset that PerTenantProjectionRebuilder drives, which the grounding pass confirms on disk rather than assuming,
  - the AdminConsole gated-page and focused-composition pattern: the Projection Status Dashboard slice as the in-repo precedent for a gated AdminConsole page, and the focused-registration discipline (AddEventStoreHeadPosition, AddProjectionRoster) that keeps the console free of the over-provisioning ValidateOnBuild rejects.

Report file:line first, verbatim where structure matters, and flag any drift from how a surface is named here. No edits, no staging, no commits.

## The opener: Phase 12 Replay Tool

The opener is the Replay Tool, Chapter 17's fourth AdminConsole operational tool and the next to build. At a high level it replays events and rebuilds projections, behind the AdminConsole fallback gate, and it promotes PostgresOrderThroughputStore.TruncateAsync from a live no-op to a real implementation, fixed RED-first.

Do not over-specify the design. The slice opens with a grounding pass, and the design forks resolve in the orchestrator loop before any write.

Known production concern, first-order for the loop: a replay that truncates and rebuilds is a destructive operation. Its safety posture is a design question to settle before the slice opens, covering what it truncates, how a partial failure mid-rebuild is handled so the read model is not left half-rebuilt with an advanced checkpoint, and whether it is gated beyond the host fallback. A destructive operator action may warrant a confirmation step or a permission stricter than read-only console access, decided at this consumer per ADR 0028's per-permission-grain prescription.

## Residual ledger

- Phase 12's four AdminConsole deliverables: the Projection Status Dashboard is complete; the Replay Tool is the active next slice; the Event Store Browser and the Correlation-ID Tracer remain.
- PostgresOrderThroughputStore.TruncateAsync is a live no-op against its IOrderThroughputStore contract, which states it drops every bucket, at PostgresOrderThroughputStore.cs:77; the siblings persist and read. This slice promotes it RED-first: a test that replays the throughput projection and asserts the read model empties, failing today, then the GREEN truncate. The overclaiming "RED #4 placeholder" headers on the class and unit-of-work are corrected in the same slice.
- The Phase 11 cross-tab done-when, that the admin dashboard's metrics match what the AdminConsole tools show, carries forward as a Phase 12 exit condition. The AdminConsole side now partly exists: the Projection Status Dashboard renders projection lag, so the comparison has one half on disk.
- Seconds-behind, the lag reader's deferred metric, and last-error, the dashboard's deferred column, both await their substrate. read_models.projection_checkpoints carries projection_name and position with no timestamp or error column, so neither has a source today.
- Gate-findings debt (no scripted voice gate, the SQL false positives in the voice grep, the manifest baseline convention) carries forward unchanged.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write, with named rejected alternatives and the load-bearing decisions stated before any write block. RED and GREEN are separate turns; show the RED failing for the intended reason, reported verbatim, before any production logic. Green-on-write guards are declared as such.
- A theater RED, where the production code already satisfies the behavior and a GREEN would write nothing, is committed as characterization with its provenance named, rather than presented as a RED.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision.
- Production quality over teaching clarity (ADR 0025), under the mandate at the top of this prompt.
- Commit lifecycle, in order: build clean under TreatWarningsAsErrors, named test run, full solution-wide dotnet test as the composition-drift gate (never per-project), voice grep, attribution grep (both on the staged added lines using the voice-grep pipeline below), diff stat, commit, push as the explicit named step, CI runs on the push, CI read to completion before the next work stacks. CI in this repo is push-triggered; no run exists for an unpushed commit.
- CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD. The ancestor case, the in-progress case, and the absent case each stop and surface to the orchestrator loop rather than passing.
- Voice grep, exact pipeline:

```
git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"
```

  The exclusion is the line-number-prefixed form; the bare triple-plus form is dead after grep -n and passes silently.
- Flake ledger: (1) OrderPlacementEndToEndTests assertion race, re-run authorized. (2) Docker Hub registry pull timeout, re-run authorized; triage CI red by exception type first. (3) PostgresHubBackplaneConnectionTests and PostgresOrderListStore LISTEN OperationCanceledException, a delivery-wait timing flake. On a red CI run, capture the failure detail and report to the orchestrator loop; the re-run decision is made there, one authorized re-run per documented event.
- Voice gate, hard, on repo artifacts: no em-dashes, no ASCII double-hyphen as a dash, no filler intensifiers, no hedges, no corporate-fluff verbs, no "not X, Y" inversions, no AI attribution in commits, ADRs, docs, or session logs. Prefer neutral framing.
- Session-meta discipline: the close doc is committed and pushed before the session ends; scripts/manifest.sh against the last-synced baseline with the verify flag runs at the session-meta landing, the baseline read from git log; the planning workspace is hand-refreshed from the manifest output; a fresh next-session prompt is authored and uploaded at close.

## First move

Produce the START resume pre-flight block, grounded from disk. Bring the output back. Do not open the Replay Tool slice or propose its design until the pre-flight reads true and the destructive-operation safety posture and the other design forks are resolved in the orchestrator loop.
