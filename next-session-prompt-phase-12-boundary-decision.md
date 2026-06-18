# Next session: Phase 12 boundary decision (Phase 11 closed)

Repo HEAD at session start: the session-meta commit that lands this prompt and the 0041 close doc, one past e882b23 on main. CI green on e882b23 (run 27701774140). Verify HEAD and CI against disk before trusting any of this; disk wins.

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

- reads the three working-pattern docs in full (first action),
- prints HEAD, tree state, 0/0 against origin/main, and the latest main CI conclusion, with no expected values baked in,
- grounds the Phase 12 boundary against disk via targeted questions.

### What the pre-flight must establish

- The operational tables as raw metrics material, with file:line from migrations/: read_models.projection_checkpoints; event_store.outbox (the sent_utc IS NULL partial index, attempt_count, last_error) and event_store.outbox_quarantine; event_store.delayed_commands and its quarantine; the events tenant_id generated column and its index.
- The order-throughput read side as it stands at HEAD: GetOrderThroughput, IOrderThroughputStore, PostgresOrderThroughputStore (including the TruncateAsync no-op named in the residual ledger), and OrderThroughputProjection with its 300-second RetentionWindow.
- The sentinel surface after ADR 0037: CollectionResourceIds members, ResourceRouting's map, SubscriptionResourceType's members, and the enum-coverage gate.
- The AdminConsole host placeholder as it stands, and what PLAN.md scopes for Phase 12. Report only; no design.

## The opener: the boundary decision (not an implementation slice)

Phase 11 is closed and the next session sits on a phase seam. The opener is a decision to resolve in the orchestrator loop before any slice opens, not an implementation slice.

Two questions to resolve:

- (a) Does the deferred operational-reader (projection lag, outbox depth) land now as its own ADR-shaped slice? It has two pending consumers: the Phase 11 cross-tab done-when, that the admin dashboard's metrics match what the AdminConsole tools show, and Phase 12 AdminConsole. The second-consumer bundling check is tripped, so building it now is need-driven rather than speculative.
- (b) Do the operational metrics ship in Phase 11 or partially wait for Phase 12?

Resolving this prevents Phase 12 opening cold against an undecided substrate.

## Candidate slices behind the decision (do-not-open until opened)

- Phase 12, the AdminConsole host.
- The IApiClient Timeout slice, named by ADR 0036's trigger as its own slice.
- The tests/PropertyTests empty-directory coverage gap, before Phase 16.
- The remaining phases per PLAN.md: Phase 13 (KurrentDB adapter), Phase 14 (DynamoDB adapter), Phase 15 (versioning and snapshots), Phase 16 (migration tooling), Phase 17 (documentation and reconciliation).
- Behind code completion: the book-repo flag-ledger pass, then Phase 17.

## Residual ledger

- PostgresOrderThroughputStore.TruncateAsync is a live no-op against its IOrderThroughputStore contract, which states it drops every bucket.
- The class and unit-of-work headers carry stale "RED #4 placeholder" prose.
- Gate-findings debt stands: no scripted voice gate, the SQL false positives in the voice grep, and the manifest baseline convention.
- The ThroughputDashboard page sets the denied flag in LoadAsync (OnInitializedAsync) and reads it in OnAfterRenderAsync(firstRender). This assumes the load completes before the first interactive render sets the badge, which holds on the current server-interactive circuit model and is pinned by B9. If a prerender pass is ever introduced, the denied-before-arm ordering wants re-checking.

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md and preamble, session prompt.
- Propose before write. RED and GREEN are separate turns; show the RED failing for the intended reason before any production logic. Green-on-write guards are declared as such.
- Abort and surface, never silently resolve. A finding that revises the frame revises the decision.
- Production quality over teaching clarity (ADR 0025).
- Commit lifecycle, in order: build clean under TreatWarningsAsErrors, named test run, full solution-wide dotnet test as the composition-drift gate (never per-project), voice grep, attribution grep (both on git diff --cached -U0 added lines), diff stat, commit, push as the explicit named step, CI runs on the push, CI read to completion before the next work stacks. CI in this repo is push-triggered; no run exists for an unpushed commit.
- Voice grep, exact pipeline:

  git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"

  The exclusion must be the line-number-prefixed form; the bare triple-plus form is dead after grep -n and passes silently.
- Flake ledger: (1) OrderPlacementEndToEndTests assertion race, re-run authorized. (2) Docker Hub registry pull timeout, re-run authorized; triage CI red by exception type first. (3) PostgresHubBackplaneConnectionTests and PostgresOrderListStore LISTEN OperationCanceledException, a delivery-wait timing flake. On a red CI run, capture the failure detail and report to the orchestrator loop; the re-run decision is made there, one authorized re-run per documented event.
- Voice gate, hard, on repo artifacts: no em-dashes, no ASCII double-hyphen as a dash, no filler intensifiers, no hedges, no corporate-fluff verbs, no "not X, Y" inversions, no AI attribution in commits, ADRs, docs, or session logs. Prefer neutral framing.
- Session-meta discipline: the close doc is committed and pushed before the session ends; scripts/manifest.sh against the last-synced baseline with the verify flag runs at the session-meta landing, the baseline read from git log; the planning workspace is hand-refreshed from the manifest output; a fresh next-session prompt is authored and uploaded at close.

## First move

Produce the START resume pre-flight block, grounded from disk against the Phase 12 boundary. Bring the output back. Do not open any slice or propose a design until the boundary decision is resolved in the orchestrator loop.
