# Next session: Phase 16, migration tooling

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by default
and without being asked, because this reference implementation ships to readers for
production use and runs in production environments. Re-derive from production-quality
first principles rather than settling on exemplar parity, teaching clarity,
cohesion-aesthetics, or convenience.

## Repo state

Phase 15 closed at the 0054 session close, on all three done-when lines at
PLAN.md:496-498, reconciled to suite reality. This prompt bakes no HEAD and no CI run
id: the resume pre-flight reconciles both from disk and applies the equality gate
itself. Read docs/sessions/0054-phase15-versioning-and-snapshots-close.md for the
arc's ledger, its refutations, its residuals, and its flags. Disk is authoritative
over this prompt.

## Scope

Migration tooling, per PLAN.md's Phase 16 section at :500-518. Goals at :502-509: a
standalone example separate from the main domain; a simulated legacy CRUD database, a
small SQL schema representing a CRUD-shaped order system; a CDC pattern where a process
reads legacy table changes from a change-tracking table and emits domain events; an
outbox-on-legacy pattern where a legacy code path writes to an outbox table inside the
legacy database with an event-emitter draining it; a strangler pattern example, a
feature implemented twice, once in legacy CRUD and once in event-sourced code, with
traffic routing between them; a shadow-mode example, events emitted in parallel to
legacy writes and compared for correctness; and a README explaining each pattern, when
to use it, and its trade-offs. Out of scope at :511-512: real production migration
scenarios, since the example is a teaching artifact. Done-when at :514-517: the
migration folder runs as its own demo with docker compose up; a reader can run it and
watch CRUD changes turn into events through each pattern; and each pattern has at least
one test demonstrating correctness.

This prompt frames the session. It does not pre-rule it. Every fork below opens in that
session's own loop, against that session's own grounding.

## Inheritances, as facts with disk locations

- **Migration is new ground, and the folder the layout reserves for it is where the
  phase lands.** CLAUDE.md's folder layout reserves src/Migration as the standalone
  Chapter 18 example, and the plan maps Phase 16 to it. This is the first phase since the
  domain work that opens on new ground rather than extending the event-store core, so its
  pre-flight grounds src/Migration's actual state, whether scaffold or empty, before
  scoping rather than assuming it.
- **The event-sourced half of the strangler already exists, and the migration example
  reuses it rather than rebuilding it.** The Order aggregate, its command handlers, the
  four event-store adapters behind IEventStore, and the outbox-driven projection path are
  all shipped and pinned. A strangler that runs a feature once in legacy CRUD and once in
  event-sourced code has the event-sourced side already; what is new is the legacy CRUD
  side, the change-tracking read, and the routing between them. Whether the example
  composes the real Application stack or a reduced copy of it is that session's grounding.
- **The outbox pattern is shipped inside the event store, and outbox-on-legacy is a
  second instance of the shape rather than the same code.** src/Infrastructure/Outbox and
  the relational adapters carry an outbox table drained in the same transaction as the
  append. The plan's outbox-on-legacy pattern writes to an outbox table inside the legacy
  CRUD database with its own emitter. Whether the example shares the drain machinery or
  stands a legacy-local copy up, and how ADR 0004's self-containment rule reads across a
  teaching artifact rather than an adapter, is open.
- **The versioning and snapshot seams landed this arc, and the migration example does not
  touch them but a reader arrives from them.** src/Infrastructure/Versioning holds the
  read-time upcaster pipeline and the shared serialization seam; the Order aggregate
  carries a snapshot memento and a snapshotting repository composes on every engine (ADR
  0050 and ADR 0051). A CDC or shadow-mode example emits the same OrderDrafted the rest of
  the system versions, so the events it produces round-trip through the same seam. The
  migration folder is downstream of those seams, not a change to them.
- **The property project exists now, and a migration correctness test has a home for its
  invariants.** tests/PropertyTests is a real project with FsCheck 3.3.3 pinned (the
  latest FsCheck.Xunit, which targets xUnit v2). A shadow-mode comparison or a CDC
  round-trip is the kind of correctness claim a property can pin, so the phase opens with
  the property leg already available rather than absent.

## Open forks for that session's loop, named as open

- **Whether the legacy CRUD database is a fifth container or rides an existing one.** The
  plan asks for a simulated legacy CRUD database as a small SQL schema. Whether it stands
  its own PostgreSQL or SQL Server container up, reuses the read-model database with a
  separate schema, or runs in the demo's own compose file separate from the test
  Testcontainers is open, and it decides how the done-when's docker compose up demo relates
  to the suite's Testcontainers.
- **Whether the five patterns are five demos or one demo with five modes.** CDC,
  outbox-on-legacy, strangler, and shadow mode are four distinct migration shapes, plus the
  simulated legacy database under them. Whether the folder is one runnable demo that
  exercises each pattern in turn or several is open, and it bears on the README's structure
  and the docker compose up entry point.
- **Whether the correctness tests are integration tests or property tests or both.** The
  done-when asks each pattern have at least one test demonstrating correctness. A shadow-mode
  comparison is a natural property, a CDC round-trip is a natural integration test against a
  real change-tracking table, and which each pattern gets is that session's ruling against
  TDD_RULES section 1's scope.
- **How the standalone example relates to the solution and the CI gate.** The plan says
  standalone and separate from the main domain. Whether src/Migration is a solution project
  under the composition-drift gate, a separate solution, or a folder outside the build is
  open, and it decides whether the migration work moves the suite count at all.

## START: resume pre-flight (read-only)

One pasteable block that reads the working-pattern docs in full first (CLAUDE.md,
CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md); prints HEAD, tree state, and ahead/behind,
and applies the CI equality gate with no baked values (newest completed run on main,
conclusion success, headSha equal to HEAD exactly; ancestor-green never clears; an
in-progress run is a stop state, not a pass); and cross-checks
docs/sessions/0054-phase15-versioning-and-snapshots-close.md as the newest close doc,
verifying on disk the claims this phase builds on.

Then it grounds, file:line first and verbatim where structure matters: src/Migration's
actual state, as the folder the plan maps Phase 16 to; the Order aggregate, its command
handlers, and the Application composition, as the event-sourced side a strangler reuses;
src/Infrastructure/Outbox and the relational outbox tables, as the shape an
outbox-on-legacy pattern is a second instance of; the docker/ compose files and the
Testcontainers fixtures, as what a docker compose up demo relates to; PLAN.md's Chapter
18 mapping and any existing src/Migration or migrations content, as the ground the plan
assumed; and the state of the solution's project list against whether a standalone
example joins it. Counts and line numbers in this prompt are starting points; reconcile
every one against disk and report drift explicitly. Do not open the write work until the
pre-flight reads true in the loop.

## Working-pattern rules (hold all)

Disk over docs over this prompt. Propose before write with named rejected alternatives.
RED and GREEN separate turns, RED verbatim, and a RED's mechanism is a claim to verify
rather than a fact to assume: confirm the failure is the one the header names before
writing production code. Characterizations declared with provenance, and a
characterization that cannot fail against any shipped shape earns its label by proving
its teeth against a scratch broken shape that is then discarded. An absence fact needs a
liveness fact beside it, or it passes against a mechanism that never ran. Fix shapes are
ruled as invariants; the executor derives the edit against every reader on disk. Grounding
that refutes a ruled composition surfaces rather than improvises, and a refutation that
would need a fact-body edit halts the turn for the loop to rule, never edits the assertion
silently. A ruled STOP whose resolution the ruling already implies may be carried through
bounded and flagged in the carrying report; a STOP whose resolution is open still halts.
Fix-forward on a red main only when bounded, locally proven, and flagged in the carrying
report. Test reach follows TDD_RULES section 3's preference order, and never a timing or
delay seam in a hot path. Full commit lifecycle in order: build clean under
TreatWarningsAsErrors, named test run, solution-wide dotnet test as the composition-drift
gate, pre-stage voice check, stage, voice grep, attribution grep, six-class
classification, diff stat, commit, push as its own step, CI read to completion under the
equality gate. Flake ledger per 0049 and 0050; a container failure is a finding, never an
assumed flake. Session-meta md-only commits may share one solution-wide gate run, with the
sharing named in the report. Destructive commands name absolute paths, never globs, joined
to any path-changing prefix by && rather than a newline.

The CI read is a wait and a verdict, and they are separate steps. Validate the run before
watching it: the id must be non-empty and its headSha must equal the pushed HEAD, found by
climbing the ladder (branch listing, then the commit's check-runs, then the runs filtered
by head SHA) and sniffing every response before parsing it, because a body that does not
start with a brace or a bracket is the surface being down rather than an answer. Read the
watch's exit from the watch itself, never through a pipe, and a watch that hangs is
abandoned rather than trusted. Take the verdict from a separate equality read, because the
watch's exit is 1 for a failed run and 1 for a broken watch and nothing distinguishes them.
An unreadable gate is unknown rather than presumed, and an incident is waited through and
reported rather than re-run around. A tip in two states is a halt.

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

Six voice-gate false-positive classes: T-SQL double-hyphen comment tokens;
case-insensitive working-pattern filename references in the attribution grep; verbatim
quotations of the gate pipelines themselves; XML comment delimiters in project files
(delimiters only, never the prose between them); non-prose CLI flag string literals in
command arrays or fenced documentation blocks, such as a container builder's insecure and
mem-db flag arguments or a documented command's own flags (the flag literal only, never
surrounding prose); and Markdown table delimiter rows, lines consisting solely of pipes,
hyphens, colons, and whitespace (the delimiter row only, never cell prose). The classes
and both pipelines live in CLAUDE_CODE_PREAMBLE.md as of Phase 14, so they survive this
prompt's deletion.

## After the phase

Phase 17, documentation, reconciliation, and polish at PLAN.md's Phase 17 section, opens
on its own pre-flight. The book-repo flag-ledger pass remains owed ahead of Phase 17 and
is scheduled as a standalone session before Phase 17 planning opens, taking the 0050,
0051, 0052, 0053, and 0054 cross-track flags as its input.
