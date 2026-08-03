# Session prompt: Arc E, manuscript reconciliation

Book repo. Root: `~/Documents/GitHub/event-sourcing-cqrs-book`. Code-repo reads by absolute path,
read-only.

This prompt opens Phase 17's Arc E. It bakes no HEAD, no CI run id, no suite count, and no flag
count. Every one of those is reconciled from disk by the pre-flight described at the bottom,
because a prompt that carries them goes stale the moment anything lands and an executor that
trusts a stale figure reports on a repository that no longer exists.

## The mandate

Production-grade correctness, rigor, and operational hygiene govern every line of the reference
implementation, and the manuscript depicts what that implementation does. The code
repo's `CLAUDE.md` carries the first half in full under "Production quality is non-negotiable"
and both repos carry the second under "Source-of-truth hierarchy". Neither is restated here.

## The arc's ledgers

Two, and they are read rather than trusted.

`docs/sessions/cross-track-flags-summary.md` in the book repo is the worklist. Arc D repaired it:
its citations are content-form, its status column was audited row by row against disk, and four
rows that read open were closed. Twenty rows read open at that repair and five read deferred, of
which four carry re-sited triggers naming an Arc E read as the thing that settles them.

The code repo's `docs/sessions/0058-phase17-arcs-b-c-and-d-close.md` is the close for the three
arcs before this one. It carries the rulings, the residual ledger, the learnings, the divergences,
and the cross-track flags this arc inherits. It is immutable history and is never edited.

Both ledgers' entries are claims about disk at the moment they were written, not facts about disk
now. That is a recorded learning in both repositories, earned more than once. Verify each entry
before acting on it.

## What makes this arc different

Every arc before this one changed documents in the repository whose code was the authority. Arc E
inverts that. The work lands in the book repo and the authority lives in the code repo, so every
reconciliation is a read of the implementation followed by an edit to the manuscript, never the
other way around. When the manuscript and the code disagree about an observable artifact, the
code is canonical and the book normalizes to match.

Three carve-outs, and they are the reason this arc cannot be run as a mechanical sweep.
Deliberate historical-shape pedagogy stays as written: Chapter 11's upcasting progression depicts
an event evolving across schema versions and its early shape is intentionally divergent, and
Chapter 18's legacy-CRUD depictions are intentional. Pedagogical divergence is exempt;
current-state divergence is not. Deciding which one a given passage is cannot be done from a
grep.

## The drafting split

Settled, and it is the shape the reconciliation arcs have used since the Chapter 8 pass. The
planning conversation drafts manuscript prose with the voice rules applied against the surrounding
chapter, because voice work needs the neighbouring paragraphs visible. The executor inserts it
mechanically using the build helpers, runs the two-pass build, and verifies against the triad.

Where drafted prose does not fit the site it is going into, the executor stops and surfaces the
misfit rather than adapting the prose. Adapting it is drafting, and an executor drafting
manuscript prose is the failure the split exists to prevent.

## The book repo's first obligation

Before any manuscript work, two edits to that repo's working-pattern documents:

1. The shared-class pairing rule's book-repo half. The rule says a change to a gate element both
   repos share lands in both in the same turn; it was written in the code repo alone because that
   commit's scope was code-repo only, so it violates itself on landing. The book-repo paragraph
   lands with a note that the pair was split across turns by scope rather than by drift.

2. The `04df1b0` correction. That repo's `CLAUDE.md` and its session log 0023 both say no session
   log covers that commit. The code repo's 0057 close does cover it. What is true is narrower:
   no book-repo log covered it, and its classified gate output is recorded nowhere. The
   log-timing rule stands on the second half and its worked example needs the first half
   corrected.

Neither touches a content module, so neither needs the two-pass build.

## Working pattern

Carried by reference, not duplicated here. A prompt that restates the governing documents creates
a second copy that drifts, which is a defect this phase has now repaired in four places.

- The book repo's `CLAUDE.md` carries its repo-wide rules, the propose-before-writing discipline,
  the manuscript edit mechanics, the verification triad, the session-log obligation with its
  same-session landing rule, and the authoritative em-dash carveout's address.
- The book repo's `HANDOFF.md` carries project state, the build pipeline, the authoritative voice
  rules, and the pre-flight protocol family.
- The book repo's `CLAUDE_CODE_PREAMBLE.md` carries the voice and attribution gate pipelines with
  their seven false-positive classes, the workspace and executor naming conventions, and the
  verbatim relationship to the code repo's gate section.
- The code repo's `CLAUDE.md` carries the source-of-truth hierarchy that governs every
  reconciliation this arc makes.

Read the three book-repo documents in full at session start. Where a block in this session
contradicts one of them, surface the conflict before acting.

That repo runs no CI. Its verdict of record is the classified gate output plus the completed
push, and its close doc is where that verdict becomes durable.

## The first move

A read-only pre-flight rooted in the book repo. Read, report, stop. No proposal, no plan, no
edit.

Reconcile from disk: both repos' HEADs and whether each tree is clean and in sync with origin;
the manuscript's current page count from the built artifact rather than from any document; the
open and deferred rows in the ledger, counted from the table rather than from any summary
sentence; which of those rows are manuscript-actionable and which name a code-repo artifact as
the drifted side; and the two obligations above, verified as still owed.

Then scope the first cluster. Cluster by chapter, since a chapter is the unit the voice work and
the build verification both operate on, and name which ledger rows it discharges.

Report what is true from disk rather than from what any document claims, and name any divergence
between the two as a finding.
