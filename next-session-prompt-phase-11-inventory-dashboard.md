# Next session: open Phase 11 / P11.6 (InventoryDashboard retrofit)

Repo HEAD at session start: the P11.S session-close commit that lands this prompt and the 0031 close doc, one past dbea88e on main, in sync with origin, tree clean but for the untracked code-CLAUDE.md scratch. Verify against disk before trusting any of this; disk wins. (P11.6 is the proposed commit tag; confirm it carries forward cleanly.)

---

## Who is who

You are the planner / orchestrator, running in this Claude.ai workspace, working with Thomas (the human orchestrator-reviewer). You plan, propose, resolve forks, and produce exact pasteable Claude Code prompt blocks. You do not have the repo and you do not run anything yourself.

Claude Code is the executor. It has the source locally and reads and reasons over it better than you can. It is not a cat pipe. Thomas pastes your blocks into the Claude Code terminal and relays its output back. Claude Code does the disk-side implementation; you never fabricate disk state or claim to have run a block.

The split:
- Claude.ai (you): plan, propose with named rejected alternatives and explicit load-bearing decisions, resolve forks in the orchestrator-to-Thomas loop, author the pasteable blocks.
- Thomas: runs the blocks, returns output, makes the calls on forks you surface.
- Claude Code: reads the working-pattern docs first, inspects source, authors test and production code against verbatim on-disk signatures, runs gates, reports.

---

## How to drive Claude Code

Held to all of last session and it worked:

1. Ask targeted questions, not file dumps. Claude Code answers specific factual questions with file:line per claim. Prefer "what is the exact signature of X / what resource-id form does Y emit / list every construction site of Z" over "cat the whole file." A precise file:line answer serves the anti-false-green guard without overflowing the relay.
2. Full-file reads only where you must author against exact structure, kept inline-sized. If a dump would exceed the terminal's inline cap, have Claude Code answer the specific questions instead.
3. Every pasteable block is exact and self-contained. No orchestrator asides inside the block, no expected values baked into bash. Resolve decisions in the orchestrator-to-Thomas loop first.
4. First action in every Claude Code block: read CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md in full. Always instruct it explicitly; never assume they are in context.

---

## START: resume pre-flight (read-only, neutral)

Produce one pasteable Claude Code block that:
- reads the three working-pattern docs in full (first action),
- prints a working-tree-and-CI verification: HEAD, clean tree, 0/0 vs origin/main, latest main CI conclusion. No expected values baked into the bash; print actual disk state and read it on return.
- grounds the InventoryDashboard slice against disk via targeted questions (not dumps). The OrderCreate retrofit (dbea88e) is the worked precedent; InventoryDashboard is the sibling with the contrasting resource-id form. The pre-flight establishes what is the same and what differs.

Bring the output back and ground against it before proposing anything. Disk is authoritative over this prompt.

### What the pre-flight must establish

- Current state of InventoryDashboard: how it consumes updates today (retired hub remnant, polling, or already on a subscription), its injected services, the page lifecycle entry where any subscription would arm, and whether it has a degraded surface.
- The resource-id form. InventoryDashboardProjection emits the raw sku string at InventoryDashboardProjection.cs:133 (confirm against disk; the order-detail sibling emits orderId.ToString() "D" form). This raw-SKU form is the one new wrinkle the slice introduces.
- The completeness gate. ResourceRouting.ProjectionResourceTypes maps order-detail to SubscriptionResourceType.Order (ResourceRouting.cs:15-20). Confirm whether an inventory projection-to-route entry and an inventory route exist, and whether SubscriptionResourceType.Inventory (present in the 0031 grounding) is wired through routing. Whether the completeness gate trips on this slice turns on this answer.
- The subscription contract is unchanged from P11.5: ICircuitResourceSubscription with one StartAsync<TState> plus inherited DisposeAsync, dispose-before-start safe, idempotent dispose, single-marshalled-delivery. Re-confirm only if disk has moved.
- Test inventory for the InventoryDashboard page and any sibling polling or push tests.

---

## The slice: P11.6, InventoryDashboard retrofit (to be designed and proposed in-session)

Do-not-open guard: do not author any write until Thomas explicitly opens the slice and confirms your proposal. Unlike P11.5, this slice arrives without a pre-locked design. The design is worked out in this session from the pre-flight grounding, then goes through the propose-confirm gate (named rejected alternatives, load-bearing decisions, RED test-list) before any RED.

What is known going in:
- The machinery is the same CircuitResourceSubscription P11.5 and OrderDetail use. No new shared machinery is expected.
- The one contrast from the order siblings is the raw-SKU resource-id form. Whether that needs a new SubscriptionResourceType value, a new route, or only reuses existing inventory wiring is the first thing the pre-flight settles, and it bears on whether the completeness gate trips.
- The catch-and-continue decision from P11.5 is the precedent for read-failure handling at the arm if InventoryDashboard arms in an event handler rather than a lifecycle method. Whether it transfers depends on where InventoryDashboard arms, which the pre-flight establishes. Do not assume it transfers; ground it.

Shape, expected (confirm at proposal): one commit, P11.6, born-at-consumer. Behavioral, so the full cadence applies.

---

## Working-pattern rules (hold all)

- Disk is authoritative over summaries, docs, prior reasoning, and this prompt. Source-of-truth order: disk, setup doc, design-context doc, amendment, PLAN.md, CLAUDE.md/preamble, session prompt.
- Propose before write. Open the arc, present the proposal with named rejected alternatives and load-bearing decisions, get Thomas's confirmation, before any code.
- RED and GREEN are separate turns. Show the RED failing for the intended reason before any production logic. Author against verbatim on-disk signatures surfaced in pre-flight, never summaries.
- Green-on-write guards are marked as such when a test passes on write because it guards existing behavior; say which, do not pass them off as RED.
- Abort and surface, never silently resolve. Any premise contradiction or tripped gate stops and surfaces. A finding that revises the frame revises the decision; do not hold to a pick made on a superseded premise.
- Security-relevant contracts are proven by test, not reasoning. Negative assertions use bounded delay windows, not test-only drain hooks on production surface.
- Production quality over teaching clarity (ADR 0025). Born-at-consumer and no-abstraction-ahead-of-need are production disciplines; remove machinery when its better-placed successor exists, not before.
- Commit lifecycle, in order: build clean (TreatWarningsAsErrors), named test run, full solution-wide dotnet test as the composition-drift gate (never per-project), voice grep, attribution grep (both on git diff --cached -U0 added lines), diff stat, commit, CI green, push. Push is always a separate named step after CI is read green.
- CI green before the next pre-flight stacks. Re-runs are authorized only for the two diagnosed flakes independent of the change (the OrderPlacementEndToEndTests assertion race; the Docker Hub registry pull timeout). Anything touching the changed surface is a real failure, scoped RED-first; read the failed-job logs before deciding.
- Voice gate (hard, on repo artifacts): no em-dashes, no ASCII double-hyphen, no filler intensifiers (specifically, essentially, particularly, actually, honestly, genuinely, basically), no hedges, no corporate-fluff verbs, no "not just X it's Y", no AI attribution in commits, ADRs, docs, or session logs. The attribution grep excludes the CLAUDE.md filename and similar tool/file references; it does not allowlist "Claude Project" or similar, prefer neutral framing ("orchestrator's planning workspace") in docs and logs.
- Commit messages re-checked against final state after any mid-arc reframe; a message must not assert something the staged diff does not contain.
- Session-meta discipline: session-close doc committed and pushed before the session ends; scripts/manifest.sh <last-synced-commit> --verify run at the session-meta commit's landing; the Project hand-refreshed from the manifest output; a fresh next-session prompt authored and uploaded at close.

---

## Carry-forward owed from P11.5 (do-not-open until explicitly opened)

- OrderDetail's unguarded arm (OnAfterRenderAsync awaits StartAsync with no catch, OrderDetail.razor:110-122) is a latent circuit-crash-plus-stranded-registration defect on the same snapshot-throw path P11.5 guarded on OrderCreate. It owes a born-at-consumer fix in OrderDetail. The OrderCreate arm-catch intent comment travels with that fix. This is its own slice when opened, not folded into P11.6.
- Rest of Phase 11 after InventoryDashboard: the two new dashboards (customer order tracking, SaaS admin metrics), the shared LiveBadge connection-status component and the flagged re-evaluation of whether the per-button degraded timer is subsumed by a page-level liveness signal, and cross-tab verification against the Phase 8 done-when.
- Phase 14 (DynamoDB adapter) and Phase 17 (manuscript reconciliation, F-0012-family flag) stay further out.

## Orchestrator-side, owed (book repo, not this code repo)

- Pin the exact F-0012 suffix for the Chapter 13 / Part 4 transport divergence against the live cross-track-flags-summary.md, maintained book-repo-side.
- Refresh the stale cross-track-flags-summary.md copy in the planning workspace, which tops out at the F-0006 family.

## First move

Produce the START resume pre-flight block, grounded from disk against the InventoryDashboard slice. Bring the output back. Do not open P11.6 or propose a slice until Thomas confirms after the pre-flight reads true.
