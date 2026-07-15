# Next session: Phase 14, the DynamoDB adapter

## Production-quality mandate

Production-grade software quality is the deciding factor on every fork, by
default and without being asked, because this reference implementation ships to
readers for production use and runs in production environments. Re-derive from
production-quality first principles rather than settling on exemplar parity,
teaching clarity, cohesion-aesthetics, or convenience.

## Repo state

Phase 13 closed at the 0052 session close, on all three done-when lines at
PLAN.md:453-455, reconciled to suite reality on the ruled :452 posture. This prompt
bakes no HEAD and no CI run id: the resume pre-flight reconciles both from disk and
applies the equality gate itself. Read
docs/sessions/0052-phase13-kurrentdb-adapter-close.md for the arc's ledger, its
refutations, its residuals, and its flags. Disk is authoritative over this prompt.

## Scope

The DynamoDB adapter, per PLAN.md's Phase 14 section at :457-476. Goals at :459-467:
EventStore.DynamoDb implementing IEventStore against DynamoDB; a composite key with
partition AggregateType#AggregateId and sort Version; conditional writes with
attribute_not_exists(Version) for optimistic concurrency; a Global Secondary Index
for global ordering and replay; the configuration switch with no domain-code change;
DynamoDB Streams plus a stream consumer, the LocalStack Lambda equivalent, feeding
projections; integration tests against LocalStack; trade-offs in code comments and an
ADR. Out of scope at :469-471: real AWS deployment, local-only via LocalStack, and
DynamoDB features beyond what the abstraction needs. Done-when at :474-476: the
existing aggregate, projection, and process-manager tests pass with the configuration
switched to DynamoDB via LocalStack; DynamoDB Streams feeds projections without
polling; and the Event Store Browser works against DynamoDB.

This prompt frames the session. It does not pre-rule it. Every fork below opens in
that session's own loop, against that session's own grounding.

## Inheritances, as facts with disk locations

- **The contract suite is split by engine capability, into three classes.**
  tests/EventStore.ContractTests carries the universal EventStoreContractTests over
  the core port contract; HeldWriterEventStoreContractTests over IHeldWriterContractBackend,
  the interactive held-open append at the engine's serialization point; and
  DuplicateEventIdRejectionContractTests. Both relational backends derive all three;
  the KurrentDB backend derives the core only, because the engine has no interactive
  transaction to park an append mid-flight, and a concurrent-append load probe stands
  in for the held-writer probes (tests/Infrastructure.Tests/Kurrent). A fourth engine
  chooses its classes by what it can express: whether LocalStack DynamoDB supports a
  held-open write at the serialization point, or routes the held-writer probes away
  as KurrentDB did, is that session's grounding to establish against the live engine.
  A green core suite on a backend whose held-writer hook is a no-op proves nothing.
- **ADR 0004's three-adapter revisit trigger has fired.** The KurrentDB adapter landed
  the third copy of the type-registry and JSON seam beside the two relational copies,
  which ADR 0047's consequences record. The trigger wording is an evaluation step, not
  automatic refactoring; the corrected cost figure is ADR 0045's 234 lines, and the
  collapse point the registries name is Phase 15's Infrastructure/Versioning. The
  fourth adapter is a named planning input for this evaluation: it is the point where a
  fourth copy either lands or the collapse happens first. This is Phase 14's decision
  to make with the trigger, the figure, and the third-copy fact in hand.
- **The non-relational companion posture is ADR 0046.** A non-relational host composes
  IIdempotencyStore and IDelayQueue as their PostgreSQL implementations on its
  read-model database through the standalone connection-string registrations, and
  IEventStore gains no methods. DynamoDB inherits this as its starting answer; ADR
  0046's trigger asks whether it holds for a second non-relational engine, or whether
  DynamoDB's TTL-plus-Streams pairing earns a native delay mechanism.
- **The subscription-service shape is the non-polling dispatch precedent.**
  src/Infrastructure/EventStore.Kurrent/KurrentSubscriptionService.cs is a
  BackgroundService that reads the engine's native change feed, plays matched events
  into the shared in-process dispatcher, and advances its own dispatch checkpoint with
  a reconnect-on-fault loop and at-least-once delivery. DynamoDB Streams feeding
  projections is the same shape against a different feed; the checkpoint's tail-lag
  characteristic (ADR 0047) is the kind of engine-specific behavior a Streams consumer
  will have its own version of.

## Open forks for that session's loop, named as open

- **DynamoDB's ordering story against ADR 0044 is the big one.** ADR 0044 binds every
  adapter to commit-ordered visibility of a single global position, with permanent
  gaps only. The relational adapters hold it with an append lock; KurrentDB holds it
  natively through its single-writer $all log. DynamoDB has no native global sequence:
  the plan proposes a GSI for global ordering, which is a different substrate with its
  own consistency and its own gap story under eventual consistency and conditional
  writes. Whether a GSI-backed global position can hold ADR 0044's invariant, and what
  its held-writer or concurrent-append probe looks like, is the phase's central
  grounding question, to be established against LocalStack and not assumed in either
  direction.
- **The companion answer for a second non-relational engine.** ADR 0046 is DynamoDB's
  starting answer, but DynamoDB pairs TTL expiry with Streams natively, which ADR 0017
  named as an expected adapter-shape change behind IDelayQueue. Whether DynamoDB reuses
  the PostgreSQL delay queue per ADR 0046 or earns a native TTL-plus-Streams delay
  mechanism is open, and it is what a DynamoDB host composition hits first.
- **Whether the registry-seam collapse happens before or during Phase 14.** The fourth
  copy is the trigger's live moment. The session rules whether to land a fourth copy
  and defer the collapse to Phase 15, or collapse the seam into Infrastructure/Versioning
  as part of composing the fourth adapter, weighed against ADR 0045's cost figure and
  the specific change driving the question.

## START: resume pre-flight (read-only)

One pasteable block that reads the working-pattern docs in full first (CLAUDE.md,
CLAUDE_CODE_PREAMBLE.md, docs/TDD_RULES.md); prints HEAD, tree state, and
ahead/behind, and applies the CI equality gate with no baked values (newest completed
run on main, conclusion success, headSha equal to HEAD exactly; ancestor-green never
clears; an in-progress run is a stop state, not a pass); and cross-checks
docs/sessions/0052-phase13-kurrentdb-adapter-close.md as the newest close doc,
verifying on disk the claims this arc builds on.

Then it grounds, file:line first and verbatim where structure matters: the contract
suite's three capability classes and their backend hooks, as the shape a fourth engine
plugs into; the KurrentDB adapter's registration surface and its TryAdd posture, as
the non-relational shape a fourth adapter is measured against; the three-copy registry
and JSON seam, as ADR 0004's fired-trigger evidence; the provider switch's host call
sites and its three per-host parser twins; the companion ports' non-relational
registration path (ADR 0046); the subscription-service dispatch shape as the
non-polling precedent; and the projection trigger seam that DynamoDB Streams must feed.
Counts and line numbers in this prompt are starting points; reconcile every one
against disk and report drift explicitly. Do not open the write work until the
pre-flight reads true in the loop.

## Working-pattern rules (hold all)

Disk over docs over this prompt. Propose before write with named rejected
alternatives. RED and GREEN separate turns, RED verbatim; characterizations declared
with provenance, and a characterization that cannot fail against any shipped shape
earns its label by proving its teeth against a scratch broken shape that is then
discarded. Fix shapes are ruled as invariants; the executor derives the edit against
every reader on disk. Grounding that refutes a ruled composition surfaces rather than
improvises, and a refutation that would need a fact-body edit halts the turn for the
loop to rule, never edits the assertion silently (Phase 13's SQ2 Option B). A ruled
STOP whose resolution the ruling already implies may be carried through bounded and
flagged in the carrying report; a STOP whose resolution is open still halts.
Fix-forward on a self-inflicted red main only when bounded, locally proven, and
flagged in the carrying report. Full commit lifecycle in order: build clean under
TreatWarningsAsErrors, named test run, solution-wide dotnet test as the
composition-drift gate, pre-stage voice check, stage, voice grep, attribution grep,
diff stat, commit, push as its own step, CI read to completion under the equality
gate. Flake ledger per 0049 and 0050; a container failure is a finding, never an
assumed flake, and the LocalStack container is the new instance of that rule.
Session-meta md-only commits may share one solution-wide gate run, with the sharing
named in the report. A background wait's exit condition reads the same stream the
watched process writes, and a foreground wait uses a run-to-completion mechanism, not
a poll-and-sleep that can outlive its turn (0052 learning).

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

Five voice-gate false-positive classes: T-SQL double-hyphen comment tokens;
case-insensitive working-pattern filename references in the attribution grep; verbatim
quotations of the gate pipelines themselves; XML comment delimiters in project files
(delimiters only, never the prose between them); and non-prose CLI flag string
literals in command arrays, such as a container builder's insecure and mem-db flag
arguments (the flag literal only, never surrounding prose).

## After the phase

Phase 15, the snapshots chapter at PLAN.md's Phase 15 section, opens on its own
pre-flight. It is the named collapse point for the registry-seam duplication ADR 0004's
trigger has flagged, if Phase 14 does not collapse it first. The book-repo
flag-ledger pass remains owed ahead of Phase 17 and is scheduled as a standalone
session before Phase 17 planning opens, taking the 0050, 0051, and 0052 cross-track
flags as its input.
