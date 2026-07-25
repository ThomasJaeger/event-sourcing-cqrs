# Next session: the book-repo flag-ledger pass

## Production-quality mandate

Production-grade quality is the deciding factor on every fork, by default and without
being asked, because this reference implementation and its manuscript ship to readers
for production use. Re-derive from first principles rather than settling on exemplar
parity, teaching clarity, cohesion-aesthetics, or convenience. For this pass the quality
axis is manuscript correctness: a flag reconciled wrong is worse than a flag left
pending, so a reconciliation that cannot be grounded against the canonical code stays
open rather than guessed.

## Repo state

Phase 16 closed at the 0055 session close, on all three done-when lines at
PLAN.md:514-517, reconciled to suite reality. This prompt bakes no HEAD and no CI run
id: the resume pre-flight reconciles both from disk and applies the equality gate itself.
Read docs/sessions/0055-phase16-migration-tooling-close.md for the arc's ledger, its
rulings, its residuals, and its flags. Disk is authoritative over this prompt, in both
repos.

This is a standalone Track-A session in the book repo at
~/Documents/GitHub/event-sourcing-cqrs-book/, not a code phase. It has been scheduled and
dated-deferred since the 0050 close and is now taken before Phase 17 planning opens. The
book repo's own working-pattern docs (its CLAUDE.md and HANDOFF.md) govern the manuscript
work; this code-repo prompt frames the pass and carries the code-repo grounding it reads.

## Scope

The book-repo flag-ledger pass over the consolidated cross-track flag index, which lives
in the book repo at docs/sessions/cross-track-flags-summary.md, not in this repo. It takes
the cross-track flags raised across the 0050 through 0055 code-repo close docs as its
input, including this arc's ADR 0050, ADR 0051, and ADR 0052 and the Phase 16 flags: the
Chapter 11 upcasting correspondence, the Chapter 12 snapshot-storage and posture drift,
the Chapter 18 migration-demo correspondence, and the PLAN.md drift lines the closes name.
Each flag is reconciled against the code, which is canonical per the source-of-truth
hierarchy, and the manuscript is normalized to match what shipped.

Code-repo work is out of scope except read-only grounding: this pass reads the code repo
to verify what a flag says the code does, and it writes only in the book repo. It produces
no code-repo commit, so the code-repo commit lifecycle below is a backstop for any
incidental code-repo touch rather than the pass's main path.

This prompt frames the session. It does not pre-rule it. Every fork below opens in that
session's own loop, against that session's own grounding, under the book repo's rules for
manuscript work.

## Inheritances, as facts with locations

- **The flag sources are the code-repo close docs, by number.** The cross-track flags this
  pass consolidates were raised in docs/sessions/0050 through 0055 in this repo, each close
  doc's "Cross-track flags" section naming its targets with chapter and PLAN.md line
  references. The 0055 close names the Chapter 18 migration-demo correspondence and the
  PLAN.md:83-86 and :88 drift lines; the 0054 close names the Chapter 11 lineage and the
  PLAN.md:488 and :487-490 snapshot-posture drift; the earlier closes carry the adapter and
  versioning flags. Read them in this repo as read-only grounding.
- **The book-repo flag summary requires this pass for the sessions after F-0006.** The
  consolidated index in the book repo is current through F-0006 and has not absorbed the
  flags raised since; the summary's own tail names where it stands. This pass is the
  backfill, so its first grounding is the summary's current tail against the code-repo
  closes' flag list, to find the gap it closes.
- **The Phase 17 arc waits behind this pass.** Phase 17, documentation and reconciliation
  and polish at PLAN.md's Phase 17 section, is the code-repo work that follows, and its
  manuscript-reconciliation half consumes the reconciled ledger this pass produces. Running
  the pass first is what keeps Phase 17 from re-deriving the flags from scratch.

## Open on purpose, for that session's loop

- **The ledger's consolidation shape.** Whether the reconciled flags fold into the existing
  cross-track-flags-summary.md structure or restructure it, and how a reconciled flag is
  marked closed against a still-pending one, is the book repo's ruling under its own
  working-pattern docs, not this prompt's.
- **The F-numbering continuation.** The flag index numbers entries F-NNNN; where the
  numbering resumes after F-0006 and how a multi-close flag that recurs is numbered is that
  session's loop, against the book repo's own convention.

## START: resume pre-flight (read-only)

One pasteable block, two-repo, read-only throughout, stop-and-surface on any drift.

First, the book repo at ~/Documents/GitHub/event-sourcing-cqrs-book/: read its
working-pattern docs in full (its CLAUDE.md and HANDOFF.md); print its HEAD, tree state,
and ahead/behind; and read docs/sessions/cross-track-flags-summary.md, reporting its
current tail, the last F-number it carries, and the last session it absorbed.

Then the code repo at ~/Documents/GitHub/event-sourcing-cqrs/: read the working-pattern
docs in full (CLAUDE.md, CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md); print HEAD, tree
state, and ahead/behind, and apply the CI equality gate with no baked values (newest
completed run on main, conclusion success, headSha equal to HEAD exactly; ancestor-green
never clears; an in-progress run is a stop state, not a pass); and cross-check
docs/sessions/0055-phase16-migration-tooling-close.md as the newest close doc.

Then it grounds the flag sources, file:line first and verbatim where structure matters:
the "Cross-track flags" section of each of docs/sessions/0050 through 0055, as the input
list; ADR 0050, 0051, and 0052 in docs/adr/, as the reasoning the flags point to; and the
canonical code each flag reconciles against, read only to verify what shipped. Reconcile
every count and line number against disk and report drift explicitly. Do not open the
manuscript write work until the pre-flight reads true in the loop and the book repo's own
pre-flight, if its docs specify one, also reads true.

## Working-pattern rules (hold all)

Disk over docs over this prompt, in both repos. For the manuscript work the book repo's
own working-pattern docs govern; the rules below govern any code-repo artifact this pass
produces, and the pass being read-only in the code repo makes them a backstop.

Propose before write with named rejected alternatives. Grounding that refutes a stated
flag surfaces rather than improvises: a flag that the code does not support the way the
close doc framed it is corrected against the code and the correction recorded, never
reconciled toward the wrong framing. A reconciliation that cannot be grounded stays
pending rather than guessed. Full commit lifecycle in order for any code-repo commit:
build clean under TreatWarningsAsErrors, named test run if code changed, solution-wide
dotnet test as the composition-drift gate, pre-stage voice check, stage by explicit path,
voice grep, attribution grep, six-class classification, diff stat, commit, push as its own
step, CI read to completion under the equality gate. Session-meta md-only commits may share
one solution-wide gate run, with the sharing named in the report. Destructive commands name
absolute paths, never globs, joined to any path-changing prefix by && rather than a newline.

The CI read is a wait and a verdict, and they are separate steps. Validate the run before
watching it: the id must be non-empty and its headSha must equal the pushed HEAD, found by
climbing the ladder (branch listing, then the commit's check-runs, then the runs filtered
by head SHA) and sniffing every response before parsing it, because a body that does not
start with a brace or a bracket is the surface being down rather than an answer. Read the
watch's exit from the watch itself, never through a pipe, and a watch that hangs is
abandoned rather than trusted. Take the verdict from a separate equality read, because the
watch's exit is 1 for a failed run and 1 for a broken watch and nothing distinguishes them.
An unreadable gate is unknown rather than presumed. A tip in two states is a halt.

Manifest baseline contract: the baseline handed to scripts/manifest.sh is the commit the
planning workspace was last synced to, which is the previous session's close-doc landing
commit, reconciled from git log with diff-filter=A on that close doc's path. It is never
the closing session's own close commit: diffing from the just-landed close treats the
session's docs as already synced and produces an empty, wrong manifest. When any directed
baseline conflicts with the script's own usage text, the script's contract wins; run both
forms, report both, and surface the conflict rather than picking silently.

Voice grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"

Attribution grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -niE "Co-Authored-By|Generated with|Claude|Anthropic"

Six voice-gate false-positive classes: T-SQL double-hyphen comment tokens; case-insensitive
working-pattern filename references in the attribution grep; verbatim quotations of
repo-historical artifacts, the gate pipelines themselves and historical commit subjects or
log lines quoted as evidence (the quoted artifact line only, never surrounding prose); XML
comment delimiters in project files (delimiters only, never the prose between them);
non-prose CLI flag string literals in command arrays or fenced documentation blocks, such
as a container builder's insecure and mem-db flag arguments or a documented command's own
flags (the flag literal only, never surrounding prose); and Markdown table delimiter rows,
lines consisting solely of pipes, hyphens, colons, and whitespace (the delimiter row only,
never cell prose). The classes and both pipelines live in CLAUDE_CODE_PREAMBLE.md as of
Phase 14, with class 3 widened to quoted historical artifacts at ffb6faa, so they survive
this prompt's deletion.

## After the pass

Phase 17, documentation, reconciliation, and polish at PLAN.md's Phase 17 section, opens
on its own pre-flight and consumes the reconciled flag ledger this pass produces. The
code-repo residuals the 0055 close carries, the migration-runner gap detection, the SQL
Server dry-run entry point, the legacy Versioning-type test move, and the open question of
whether CI builds the demo image, are Phase 17 or own-slice candidates, not this pass's.
