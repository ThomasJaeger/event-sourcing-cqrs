# Session prompt: Arc B's remainder, then Arc C

Code repo. Root: `~/Documents/GitHub/event-sourcing-cqrs`.

This prompt opens a session on the remainder of Phase 17's Arc B and hands off to Arc C. It
bakes no HEAD, no CI run id, and no suite count. Every one of those is reconciled from disk by
the resume pre-flight described at the bottom, because a prompt that carries them goes stale the
moment anything lands and an executor that trusts a stale figure reports on a repository that no
longer exists.

## The mandate

Production-grade correctness, rigor, and operational hygiene govern every line. There is no axis
on which a teaching-friendly shortcut wins. When a teaching version and the production version
diverge, the production version ships and the manuscript reconciles to it. `CLAUDE.md` carries
this in full under "Production quality is non-negotiable" and it is not restated here.

## The arc's ledger

`docs/sessions/0057-phase17-arcs-a-and-b-partial-close.md` is the ledger for Arcs A and B. It
carries what landed, the rulings and their reasoning, the residual ledger, the flake ledger, the
learnings, and the cross-track flags. Read it before scoping anything. It is immutable history
and is never edited.

Its residual entries are claims about disk at the moment it was written, not facts about disk
now. That distinction is one of the session's own recorded learnings: two entries from the
preceding close did not survive contact. Verify each entry before acting on it.

## What Arc B has left

Three items:

1. **Migration gap detection.** Neither the Postgres nor the SQL Server migration runner asserts
   contiguity or monotonicity against what is applied. Both reject duplicate versions and neither
   notices a gap. The Postgres pending selection is a per-version set difference rather than a
   watermark, so a back-filled lower-numbered migration applies out of historical order with no
   warning.

2. **The dry-run retirement.** The SQL Server migration runner has a dry-run code path and option
   with no operator entry point. There is no `EventStore.SqlServer.Cli`, and only the Postgres CLI
   sets the flag.

3. **TODO resolution**, per `docs/PLAN.md:527`.

## The fork this session rules

Whether items two and three belong in Arc B or move to the release arc. Both are
release-checklist hygiene rather than code truth, and the close doc records that the TODO item
has zero targets on disk, which makes it a verification rather than a cleanup. Rule the fork
before scoping, and record the ruling.

Item one stays in Arc B whichever way the fork goes, on the ruling that a runner applying a
back-filled migration out of order with no warning is a silent data-corruption path.

## What follows

Arc C, code-repo documentation. Then Arc D, the book-repo rules sweep. Then Arc E, manuscript
reconciliation per chapter.

**Arc C's drift set is re-derived from disk rather than assembled from any close doc's list**,
including the 0057 close's own. Close docs record what was true when written; a documentation arc
that inherits a list inherits its staleness and then normalizes prose against it. Derive the set,
then compare it to what the close docs predicted, and report the divergence as a finding.

## Working pattern

Carried by reference, not duplicated here. The governing documents now hold what earlier prompts
used to restate, and a prompt that restates them creates a second copy that drifts:

- `CLAUDE_CODE_PREAMBLE.md` carries the working pattern, the voice and attribution gate pipelines
  with their six false-positive classes, the CI wait rule as a bounded condition-loop with its
  ladder and its equality read, the manifest baseline contract, the enumeration rule for
  completeness sweeps, and the rule that a write is verified by reading the landed text.
- `CLAUDE.md` carries the repo-wide rules, the source-of-truth hierarchy, the attribution
  convention, and the confidentiality rule.
- `docs/TDD_RULES.md` carries the test-first discipline, the anti-theater enforcement, and the
  test-reach preference order.

Read all three in full at session start. Where a block in this session contradicts one of them,
surface the conflict before acting.

## The first move

A read-only resume pre-flight. Read, report, stop. No proposal, no plan, no edit.

Reconcile from disk and from the API: the current HEAD and whether the tree is clean and in sync
with origin; the commit range since the 0057 close and each commit's CI verdict by head SHA; the
current suite count and assembly count from a gate run rather than from any document; the state
of each of Arc B's three remaining items against the code that implements them; and the manifest
sync point the loop names, verified to resolve, to be an ancestor of HEAD, and not to be HEAD.

Report what is complete based on git state and disk rather than on what any document claims, and
name any divergence between the two as a finding.
