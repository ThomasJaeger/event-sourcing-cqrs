# Next session: Phase 13, the KurrentDB adapter

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by
default and without being asked, because this reference implementation ships to
readers for production use and runs in production environments. Re-derive from
production-quality first principles rather than settling on exemplar parity,
teaching clarity, cohesion-aesthetics, or convenience.

## Repo state

Phase 2 closed at the 0051 session close, on all six done-when lines at
PLAN.md:247-253. This prompt bakes no HEAD and no CI run id: the resume pre-flight
reconciles both from disk and applies the equality gate itself. Read
docs/sessions/0051-phase2-provider-switch-and-close.md for the arc's ledger, its
residuals, and its flags. Disk is authoritative over this prompt.

## Scope

The KurrentDB adapter, per PLAN.md's Phase 13 section at :439-455. Goals at
:441-447: EventStore.Kurrent implementing IEventStore over the gRPC client;
append, read, and optimistic concurrency mapped to KurrentDB semantics; the
configuration switch with no code changes outside the infrastructure layer; native
catch-up subscriptions feeding projections in place of polling; Testcontainers
integration tests; trade-offs documented in code comments and an ADR. Out of scope
at :449-450: KurrentDB features beyond what the abstraction needs. Done-when at
:452-455: the existing aggregate, projection, and process-manager tests pass with
the configuration switched to KurrentDB; native subscriptions feed projections
without polling; the Event Store Browser works against KurrentDB.

This prompt frames the session. It does not pre-rule it. Every fork below opens in
that session's own loop, against that session's own grounding.

## Inheritances, as facts with disk locations

- **The contract suite.** tests/EventStore.ContractTests, 14 facts over the stated
  port contract, abstract over a backend with two engine hooks: a held writer at
  the engine's serialization point, and a raw committed-positions read. Both
  relational backends derive from EventStoreContractTests and supply their own
  fixture (tests/Infrastructure.Tests/Postgres/PostgresEventStore_Contract_Tests.cs
  and tests/Infrastructure.Tests/SqlServer/SqlServerEventStore_Contract_Tests.cs).
  The suite carries engine-divergence probes on purpose: commit visibility through
  the held-writer hook, ADR 0044 gap tolerance in place of contiguity,
  position-only ordering, let-the-write-fail concurrency, and the untranslated
  duplicate event id. A third engine plugging into it is the test of whether the
  suite is a contract or a PostgreSQL habit.
- **ADR 0044, the commit-ordered global position**, with its July 2026 amendment
  recording the SQL Server posture. The invariant binds every adapter. Whether
  KurrentDB's native $all ordering already provides commit-ordered visibility, or
  needs an engine counterpart to the two relational adapters' locks, is for that
  session's grounding to establish against the live engine. It is not to be assumed
  in either direction, and a green suite on a backend whose held-writer hook is a
  no-op proves nothing.
- **ADR 0004, self-contained adapters.** The adapter owns its own mechanics. The
  revisit trigger is three adapters touching identical code, and the corrected
  duplication figure lives in ADR 0045.
- **ADR 0045, the SQL Server engine mappings**, as the exemplar of what an
  engine-mappings ADR records: the silent hazards rather than the compile errors,
  each with the decision it forced, plus the configuration, CI, and cost
  consequences.

## Open forks for that session's loop, named as open

- **The companion-port boundary.** The Option A ruling scoped IIdempotencyStore and
  IDelayQueue companions to the relational adapters, each shipping its own against
  its own database. What a non-relational engine supplies for those ports is open,
  and it is the first thing a KurrentDB host composition hits: the idempotency
  behavior runs unconditionally for every command, so an adapter that leaves
  IIdempotencyStore unregistered fails a host at its first one. ADR 0017 anticipates
  per-adapter delay mechanisms and names KurrentDB's native scheduled messages.
  Ruling this is a precondition for a KurrentDB host, not a footnote.
- **The provider key's recognized set.** EVENT_STORE_PROVIDER recognizes Postgres
  and SqlServer today, parsed by a per-host copy of the parse-or-throw helper
  (src/Hosts/Api/EventStoreProviderSelection.cs and the Workers twin), with
  AdminConsole guarding on any non-Postgres value. Where a third value's host
  branches land, and whether per-host duplication is still the cheaper honesty under
  ADR 0004's posture at three copies, is open.
- **AdminConsole's read-side ports.** They are PostgreSQL-bound behind that startup
  guard, and PLAN.md:455 requires the Event Store Browser against KurrentDB. That
  done-when line and the guard collide by construction. The session rules how, and
  rules what the head-position and correlation-trace ports become on an engine with
  no SQL.
- **Whether to spike first.** The SQL Server arc ran a throwaway spike under
  TDD_RULES section 1, and it produced two design-changing findings that no amount
  of reading would have surfaced. Whether KurrentDB warrants the same is a ruling
  for that session, with TDD_RULES section 1 as the frame. The spike is throwaway;
  it is never the deliverable.

## START: resume pre-flight (read-only)

One pasteable block that reads the working-pattern docs in full first (CLAUDE.md,
CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md); prints HEAD, tree state, and
ahead/behind, and applies the CI equality gate with no baked values (newest
completed run on main, conclusion success, headSha equal to HEAD exactly;
ancestor-green never clears; an in-progress run is a stop state, not a pass); and
cross-checks docs/sessions/0051-phase2-provider-switch-and-close.md as the newest
close doc, verifying on disk the claims this arc builds on.

Then it grounds, file:line first and verbatim where structure matters: the contract
suite's backend seam and its two engine hooks; both relational adapters'
registration surfaces, as the shape a third adapter is measured against; the
provider switch's host call sites and its per-host parser; the companion ports'
consumers on the command path; the projection trigger seam that native
subscriptions must replace; and AdminConsole's three read-side ports. Counts and
line numbers in this prompt are starting points; reconcile every one against disk
and report drift explicitly. Do not open the write work until the pre-flight reads
true in the loop.

## Working-pattern rules (hold all)

Disk over docs over this prompt. Propose before write with named rejected
alternatives. RED and GREEN separate turns, RED verbatim; characterizations
declared with provenance, and a characterization that cannot fail against any
shipped shape earns its label by proving its teeth against a scratch broken shape
that is then discarded (0051). Fix shapes are ruled as invariants; the executor
derives the edit against every reader on disk. Grounding that refutes a ruled
composition surfaces rather than improvises. A ruled STOP whose resolution the
ruling already implies may be carried through bounded and flagged in the carrying
report; a STOP whose resolution is open still halts. Fix-forward on a self-inflicted
red main only when bounded, locally proven, and flagged in the carrying report. Full
commit lifecycle in order: build clean under TreatWarningsAsErrors, named test run,
solution-wide dotnet test as the composition-drift gate, pre-stage voice check,
stage, voice grep, attribution grep, diff stat, commit, push as its own step, CI read
to completion under the equality gate. Flake ledger per 0049 and 0050; a container
failure is a finding, never an assumed flake. Session-meta md-only commits may share
one solution-wide gate run, with the sharing named in the report.

Voice grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -nE "—|--|specifically|essentially|particularly|actually|honestly|genuinely|basically"

Attribution grep, exact pipeline:

git diff --cached -U0 | grep -nE '^\+' | grep -vE '^[0-9]+:\+\+\+' | grep -niE "Co-Authored-By|Generated with|Claude|Anthropic"

Four voice-gate false-positive classes: T-SQL double-hyphen comment tokens;
case-insensitive working-pattern filename references in the attribution grep;
verbatim quotations of the gate pipelines themselves; XML comment delimiters in
project files (delimiters only, never the prose between them).

## After the phase

Phase 14, the DynamoDB adapter at PLAN.md:457-470, opens on its own pre-flight
against whatever this arc leaves on disk. It is the fourth engine, which is where
ADR 0004's three-adapter revisit trigger has already fired, with ADR 0045's
corrected cost figure as the input. The book-repo flag-ledger pass remains owed
ahead of Phase 17.
