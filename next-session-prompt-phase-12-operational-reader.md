# Next session: Phase 12 first slice, the operational reader (Phase 11 closed, boundary decision resolved)

Repo state: the P12.0.S session-meta close that records the resolved Phase 12 boundary decision (ADR 0039) is the commit that lands this prompt, and that commit is HEAD. Phase 11 is closed on its named closer, the live /admin/throughput meter. The Phase 12 boundary decision is resolved and banked in ADR 0039 and session doc 0042. Verify HEAD and CI against disk at session open before trusting any of this; disk wins. The resume pre-flight grounds the exact HEAD sha and CI state; this prompt bakes none.

## Ordering note

Book-repo work waits on a complete reference implementation. The flag-ledger pass and the Phase 17 manuscript reconciliation sit behind code completion. The deferral is recorded here so Phase 17 opens knowing the ledger work is owed first.

## Who is who

The planner and orchestrator runs in the planning workspace and works with the human orchestrator-reviewer. The planner plans, proposes, resolves forks, and produces exact pasteable executor prompt blocks. The planner does not hold the repo and runs nothing.

The code executor has the source locally and reads and reasons over it. The human orchestrator-reviewer pastes the planner's blocks into the executor terminal and relays the output back. The planner never fabricates disk state or claims to have run a block.

The split:

- The planner: plan, propose with named rejected alternatives and explicit load-bearing decisions, resolve forks in the orchestrator loop, author the pasteable blocks.
- The human orchestrator-reviewer: runs the blocks, returns output, makes the calls on forks the planner surfaces.
- The code executor: reads the working-pattern docs first, inspects source, authors against verbatim on-disk signatures, runs gates, reports.

## How to drive the executor

1. Ask targeted questions, not file dumps. Prefer file:line answers to whole-file reads.
2. Full-file reads only where authoring needs exact structure, kept inline-sized.
3. Every pasteable block is exact and self-contained. No orchestrator asides inside the block, no expected values baked into bash. Resolve decisions in the orchestrator loop first.
4. First action in every executor block: read CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full. Instruct it explicitly.
5. Path drift: the solution file is EventSourcingCqrs.slnx at the repo root and the Web host project is src/Hosts/Web/Hosts.Web.csproj. Name the on-disk forms in blocks.

## START: resume pre-flight (read-only, neutral)

Produce one pasteable executor block that:

- reads CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full as its first action,
- prints HEAD, tree state, 0 ahead and 0 behind against origin/main, and the latest main CI conclusion, with no expected values baked in,
- applies the CI equality gate: CI covers HEAD only when a completed successful run's headSha equals HEAD; the ancestor, in-progress, and absent cases each stop and surface to the orchestrator loop,
- confirms ADR 0039 is on disk and Phase 11 is closed,
- grounds the operational-reader substrate this close already verified: read_models.projection_checkpoints is global, created in migration 0003 (migration 0017 keeps it out of the tenant_id rollout); event_store.outbox carries the sent_utc IS NULL pending index with attempt_count and last_error; event_store.events.tenant_id is a generated column,
- reports the AdminConsole host state: an empty .gitkeep at src/Hosts/AdminConsole, no Program.cs, no csproj, absent from the solution.

Report file:line first, verbatim where structure matters, and flag any drift from how a surface is named here. No edits, no staging, no commits.

## The opener: Phase 12 first slice, the operational reader

The opener is Phase 12's first slice: the operational reader for projection lag and outbox depth, born at the Projection Status Dashboard, built tenant-aware where applicable, and tested standalone as its own RED and GREEN slice before the dashboard consumes it. Lag is head position minus checkpoint position; the global read_models.projection_checkpoints table is the correct substrate (ADR 0039). Operational metrics live in the AdminConsole host and stay out of the Web throughput meter (ADR 0039).

Open design questions to resolve in the orchestrator loop before any write:

- the reader's port shape and its home project,
- the presentation scoping: an all-tenant operational view against per-tenant cuts for outbox depth and events per second,
- whether the Projection Status Dashboard slice and the reader slice are separate RED and GREEN turns,
- the AdminConsole host bootstrap: src/Hosts/AdminConsole is an empty .gitkeep today with no Program.cs and no csproj, so Phase 12 stands the host up first.

## Scheduled Phase 12 item: the TruncateAsync and Replay Tool binding

When the Replay Tool slice lands, fix PostgresOrderThroughputStore.TruncateAsync RED-first: a test that replays the throughput projection and asserts the read model empties, failing today, then the GREEN truncate. Correct the overclaiming "RED #4 placeholder" headers on the PostgresOrderThroughputStore class and unit-of-work in that same slice. This is not the first slice; it is recorded here so it is not lost.

## Residual ledger

- PostgresOrderThroughputStore.TruncateAsync is a live no-op against its IOrderThroughputStore contract, which states it drops every bucket. Confirmed on disk at PostgresOrderThroughputStore.cs:76-77; the sibling methods GetBucketsAsync, IncrementSecondAsync, and PruneBeforeAsync are live. Promoted this session to a named Phase 12 work item bound to the Replay Tool slice: fix it RED-first with a test that replays the throughput projection and asserts the read model is emptied, failing today, and correct the overclaiming headers in that same slice.
- The "RED #4 placeholder" headers on the PostgresOrderThroughputStore class (lines 9-13) and unit-of-work (lines 80-83) overclaim. They claim the data-access methods do not persist. At HEAD only TruncateAsync is a no-op; GetBucketsAsync, IncrementSecondAsync, and PruneBeforeAsync persist and read. Corrected in the same Replay Tool slice.
- Gate-findings debt (no scripted voice gate, the SQL false positives in the voice grep, the manifest baseline convention) carries forward unchanged.
- The ThroughputDashboard denied-before-arm ordering note carries forward unchanged: the page sets the denied flag in LoadAsync (OnInitializedAsync) and reads it in OnAfterRenderAsync(firstRender), which holds on the current server-interactive circuit model and is pinned by B9; a future prerender pass wants this ordering re-checked.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write. RED and GREEN are separate turns; show the RED failing for the intended reason before any production logic. Green-on-write guards are declared as such.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision.
- Production quality over teaching clarity (ADR 0025).
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

Produce the START resume pre-flight block, grounded from disk. Bring the output back. Do not open the operational-reader slice or propose its design until the pre-flight reads true and the open design questions are resolved in the orchestrator loop.
