# Next session: Phase 15, versioning and snapshots

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by
default and without being asked, because this reference implementation ships to
readers for production use and runs in production environments. Re-derive from
production-quality first principles rather than settling on exemplar parity,
teaching clarity, cohesion-aesthetics, or convenience.

## Repo state

Phase 14 closed at the 0053 session close, on all four done-when lines at
PLAN.md:474-477, reconciled to suite reality on the same posture Phase 13's :453
took. This prompt bakes no HEAD and no CI run id: the resume pre-flight reconciles
both from disk and applies the equality gate itself. Read
docs/sessions/0053-phase14-dynamodb-adapter-close.md for the arc's ledger, its
refutations, its residuals, and its flags. Disk is authoritative over this prompt.

## Scope

Versioning and snapshots, per PLAN.md's Phase 15 section at :479-499. Goals at
:481-490: one worked event versioning example, a real change to an Order event
between v1 and v2; Upcaster<TFrom, TTo> infrastructure with chaining; an upcasting
pipeline that runs at read time and never mutates stored events; a schema registry
as a small in-process registry of known event schemas; the snapshot pattern applied
to the Order aggregate; a snapshot trigger every 50 events; snapshot storage in a
separate PostgreSQL table; snapshot tests proving snapshot-plus-tail equals
full-replay and a measurable rehydration speedup on long streams; and snapshot
versioning that discards and rebuilds rather than upcasts. Out of scope at :492-493:
a full schema registry server. Done-when at :495-498: old v1 events rehydrate
correctly through the upcaster after the schema change; snapshot tests demonstrate
equivalence and speedup; and the book's Chapter 11 worked example corresponds to
runnable code.

This prompt frames the session. It does not pre-rule it. Every fork below opens in
that session's own loop, against that session's own grounding.

## Inheritances, as facts with disk locations

- **The versioning seam already exists, and the plan does not assume it.** Phase 15
  is where the plan expected Infrastructure/Versioning to be created. It was created
  in Phase 14 instead, at 089d2bf under ADR 0048, when the fourth adapter turned
  ADR 0004's three-adapter trigger into a live choice between a fourth copy and a
  collapse. src/Infrastructure/Versioning today holds EventTypeRegistry,
  ProcessManagerEventTypeRegistry, TenantIdJsonConverter, UnknownEventTypeException,
  EventMetadataReader, and EventStoreJsonOptions: 751 lines of duplication collapsed
  to 306, with all four adapters composing against it. The plan's "schema registry as
  a small in-process registry of known event schemas" therefore opens against ground
  that is partly occupied. What EventTypeRegistry already is, what a schema registry
  would add beyond it, and whether they are one type or two, is that session's
  grounding to establish rather than assume in either direction.
- **Four adapters now share that seam, and versioning changes cut across all of them.**
  EventStore.Postgres, EventStore.SqlServer, EventStore.Kurrent, and
  EventStore.DynamoDb each resolve EventStoreJsonOptions and the two registries from
  Infrastructure/Versioning. A payload written on any engine round-trips on any other
  only while all four agree on the seam, which is what ADR 0048 records. An upcasting
  pipeline that runs at read time lands inside that shared seam rather than beside it,
  so its blast radius is four engines rather than one.
- **The engine mappings are recorded per adapter, and they constrain snapshots.** ADR
  0045 (SQL Server), ADR 0047 (KurrentDB, with its July 2026 amendment on equal
  positions within one append), and ADR 0049 (DynamoDB) carry what each engine does
  with positions, gaps, identity, and ordering. Snapshot-plus-tail equals full-replay
  is a claim about reading a stream from a version forward, which every adapter
  implements and the shared contract suite pins. Whether the snapshot table is
  PostgreSQL-only per the plan while three of four engines hold events elsewhere is a
  composition question the phase opens with.
- **ADR 0044 binds global position ordering, and no property test guards it.**
  tests/PropertyTests is an empty directory: no project, no solution entry, and no
  FsCheck pin anywhere in Directory.Packages.props or any csproj, though CLAUDE.md:61
  lists FsCheck in the stack. Four engines now carry ordering mappings and none is
  property-tested. Phase 15's serialization round-trip work is the first goal since
  Phase 1 that names property tests as its natural shape.

## Open forks for that session's loop, named as open

- **Whether the schema registry is EventTypeRegistry grown up or a second type.** The
  plan names a schema registry as new construction. Infrastructure/Versioning already
  resolves a storage type name to a CLR type through EventTypeRegistry, which is part
  of what a schema registry does. Whether the phase extends that type, adds a peer
  beside it, or finds the plan's registry is a different concern entirely is open, and
  it is the first thing a reader of both will ask.
- **Where the upcasting pipeline sits relative to the four adapters.** Read-time
  upcasting has to happen between the stored payload and the hydrated event. Each
  adapter hydrates its own rows today. Whether the pipeline lands in the shared
  Versioning seam, in each adapter, or at a boundary above them decides whether one
  change reaches four engines or four changes do, and ADR 0004's self-containment rule
  and ADR 0048's shared-seam rule both bear on it.
- **Whether snapshots stay PostgreSQL-only across four engines.** The plan says a
  separate PostgreSQL table. ADR 0046's non-relational companion posture already puts
  the idempotency store and the delay queue on the read-model database for KurrentDB
  and DynamoDB hosts, which is the precedent for a relational companion beside a
  non-relational event store. Whether snapshots inherit that posture, or whether a
  snapshot belongs in the engine that holds the events it summarizes, is open and
  reopens ADR 0046's trigger.
- **Whether the measurable speedup is a test or a benchmark.** The plan asks that
  snapshots reduce rehydration time measurably on long streams. A wall-clock assertion
  in a test suite is a flake generator on a shared CI runner, and this arc has the
  scar: a 4-core runner starved a retry cap that survived five of five local runs.
  What "measurably" is pinned as, and whether it is pinned as a timing at all rather
  than as a count of events replayed, is that session's ruling.

## START: resume pre-flight (read-only)

One pasteable block that reads the working-pattern docs in full first (CLAUDE.md,
CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md); prints HEAD, tree state, and
ahead/behind, and applies the CI equality gate with no baked values (newest completed
run on main, conclusion success, headSha equal to HEAD exactly; ancestor-green never
clears; an in-progress run is a stop state, not a pass); and cross-checks
docs/sessions/0053-phase14-dynamodb-adapter-close.md as the newest close doc,
verifying on disk the claims this arc builds on.

Then it grounds, file:line first and verbatim where structure matters:
src/Infrastructure/Versioning's actual contents and every call site across the four
adapters, as the seam the plan assumed did not exist; EventTypeRegistry's real
surface against what the plan's schema registry asks for; each adapter's hydration
path, as where a read-time upcaster would have to sit; the EventEnvelope and
EventMetadata shapes including EventVersion, as what a v1-to-v2 change moves; the
Order aggregate's rehydration path and its repository, as the snapshot's caller;
ADR 0046's companion posture, as the precedent a PostgreSQL snapshot table beside a
non-relational event store would inherit; and the state of tests/PropertyTests
against CLAUDE.md's FsCheck claim. Counts and line numbers in this prompt are
starting points; reconcile every one against disk and report drift explicitly. Do not
open the write work until the pre-flight reads true in the loop.

## Working-pattern rules (hold all)

Disk over docs over this prompt. Propose before write with named rejected
alternatives. RED and GREEN separate turns, RED verbatim, and a RED's mechanism is a
claim to verify rather than a fact to assume: confirm the failure is the one the
header names before writing production code. Characterizations declared with
provenance, and a characterization that cannot fail against any shipped shape earns
its label by proving its teeth against a scratch broken shape that is then discarded.
An absence fact needs a liveness fact beside it, or it passes against a mechanism that
never ran. Fix shapes are ruled as invariants; the executor derives the edit against
every reader on disk. Grounding that refutes a ruled composition surfaces rather than
improvises, and a refutation that would need a fact-body edit halts the turn for the
loop to rule, never edits the assertion silently. A ruled STOP whose resolution the
ruling already implies may be carried through bounded and flagged in the carrying
report; a STOP whose resolution is open still halts. Fix-forward on a red main only
when bounded, locally proven, and flagged in the carrying report. Test reach follows
TDD_RULES section 3's preference order, and never a timing or delay seam in a hot
path. Full commit lifecycle in order: build clean under TreatWarningsAsErrors, named
test run, solution-wide dotnet test as the composition-drift gate, pre-stage voice
check, stage, voice grep, attribution grep, six-class classification, diff stat,
commit, push as its own step, CI read to completion under the equality gate. Flake
ledger per 0049 and 0050; a container failure is a finding, never an assumed flake.
Session-meta md-only commits may share one solution-wide gate run, with the sharing
named in the report. Destructive commands name absolute paths, never globs, joined to
any path-changing prefix by && rather than a newline.

The CI read is a wait and a verdict, and they are separate steps. Validate the run
before watching it: the id must be non-empty and its headSha must equal the pushed
HEAD, found by climbing the ladder (branch listing, then the commit's check-runs, then
the runs filtered by head SHA) and sniffing every response before parsing it, because
a body that does not start with a brace or a bracket is the surface being down rather
than an answer. Read the watch's exit from the watch itself, never through a pipe.
Take the verdict from a separate equality read, because the watch's exit is 1 for a
failed run and 1 for a broken watch and nothing distinguishes them. An unreadable gate
is unknown rather than presumed, and an incident is waited through and reported rather
than re-run around. A tip in two states is a halt.

Manifest baseline contract: the baseline handed to scripts/manifest.sh is the commit
the planning workspace was last synced to, which is the previous session's close-doc
landing commit, reconciled from git log with diff-filter=A on that close doc's path.
It is never the closing session's own close commit: diffing from the just-landed close
treats the session's docs as already synced and produces an empty, wrong manifest.
When any directed baseline conflicts with the script's own usage text, the script's
contract wins; run both forms, report both, and surface the conflict rather than
picking silently.

Voice grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"

Attribution grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -niE "Co-Authored-By|Generated with|Claude|Anthropic"

Six voice-gate false-positive classes: T-SQL double-hyphen comment tokens;
case-insensitive working-pattern filename references in the attribution grep; verbatim
quotations of the gate pipelines themselves; XML comment delimiters in project files
(delimiters only, never the prose between them); non-prose CLI flag string literals in
command arrays or fenced documentation blocks, such as a container builder's insecure
and mem-db flag arguments or a documented command's own flags (the flag literal only,
never surrounding prose); and Markdown table delimiter rows, lines consisting solely
of pipes, hyphens, colons, and whitespace (the delimiter row only, never cell prose).
The classes and both pipelines live in CLAUDE_CODE_PREAMBLE.md as of Phase 14, so they
survive this prompt's deletion.

## After the phase

Phase 16, migration tooling at PLAN.md's Phase 16 section, opens on its own
pre-flight. The book-repo flag-ledger pass remains owed ahead of Phase 17 and is
scheduled as a standalone session before Phase 17 planning opens, taking the 0050,
0051, 0052, and 0053 cross-track flags as its input.
