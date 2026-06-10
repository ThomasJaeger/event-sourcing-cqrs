# Next session: book-repo flag-ledger pass (orchestrator-side)

Code-repo HEAD at session start: the P11.S session-meta commit that lands this prompt and the 0036 close doc, one past 9cf4b13 (P11.10: Translate the authorize timeout at the HTTP boundary) on main, in sync with origin. Verify against disk before trusting any of this; disk wins.

This session's primary surface is the book repository and the orchestrator's planning workspace, not the code repo. No code-repo write is planned; every code slice below sits behind a do-not-open guard.

## Who is who

You are the planner / orchestrator in the Claude.ai workspace, working with Thomas. Claude Code is the executor, and this session it runs against the BOOK repository, under that repo's own rule set. The book repo's CLAUDE.md governs there, and its required reading order at session start is: CLAUDE.md, HANDOFF.md, docs/sessions/cross-track-flags-summary.md. The code repo's working-pattern docs do not carry over. Claude Code in the book repo does not draft original manuscript prose; this pass is ledger work, not content work, and should touch no content module.

## The pass

Owed, now deferred across five closes:

1. Pin the exact F-0012 suffix for the Chapter 13 / Part 4 transport divergence against the live docs/sessions/cross-track-flags-summary.md in the book repo, the canonical flag index. The flag must reflect the Phase 11 endpoint on disk in the code repo: the runtime SignalR hub retired in favor of in-process subscription (ADR 0032 supersedes ADR 0027), page-owned liveness with the shared LiveBadge (ADR 0034), and the authorize timeout translated at the HTTP boundary (ADR 0035).
2. Refresh the stale planning-workspace copy of cross-track-flags-summary.md, which tops out at the F-0006 family. Thomas uploads the live file manually after the pass.
3. Verify against the live summary whether any flag backfill from earlier code-repo sessions is owed, grounded from the book repo's disk, not from recollection or workspace copies.

## START: read-only book-repo pre-flight

Produce one pasteable Claude Code block for the BOOK repo that:
- reads CLAUDE.md, HANDOFF.md, and docs/sessions/cross-track-flags-summary.md in full (first action, per that repo's reading order),
- prints HEAD, tree state, and the most recent session logs in docs/sessions by filename,
- reports the flag summary's structure: every flag family present by ID, the highest family, and the full F-0012 family section verbatim if present or a statement of its absence,
- proposes nothing and writes nothing.

Bring the output back and ground against it before any proposal. The book repo's propose-before-writing discipline applies to every edit there.

## Do-not-open guards (code repo)

- Snapshot-leg false-Live repair: first code slice behind this pass. ADR 0035's Consequences carries the frame; the dispatcher delivery path's exception handling must be grounded in its pre-flight before any repair shape is proposed.
- The two dashboards (customer order tracking, SaaS admin metrics) under the ADR 0033 collection-sentinel template.
- Cross-tab verification against the Phase 8 done-when, the Phase 11 closer.
- tests/PropertyTests empty-directory coverage gap, before Phase 16.
- Phase 14 (DynamoDB adapter) and Phase 17 (manuscript reconciliation) stay further out.

## Working-pattern rules

All rules from the prior session prompt hold for any code-repo work, including the commit lifecycle, RED/GREEN separation, voice and attribution gates, and the source-of-truth order with disk on top. The flake ledger is unchanged: OrderPlacementEndToEndTests assertion race (re-run authorized), Docker Hub registry pull timeout (re-run authorized), PostgresHubBackplaneConnectionTests (CANDIDATE at one data point; capture failure detail before any re-run decision). Path drift note: the code-repo solution file is EventSourcingCqrs.slnx at the repo root and the Web host project is src/Hosts/Web/Hosts.Web.csproj.

A carried gate finding: no scripted voice gate exists in the code repo; the rules live as prose and each session re-authors the grep. Pinning the invocation in CLAUDE.md or a script is a candidate small slice; decision owed at a future code-repo open.

## Manifest baseline

The next session-meta manifest references the session-meta commit that landed this prompt and the 0036 close doc, one past 9cf4b13 on main. Read its SHA from git log at run time; do not assume it.

## First move

Produce the START book-repo pre-flight block. Bring the output back. Do not propose any ledger edit until Thomas confirms the pre-flight reads true.
