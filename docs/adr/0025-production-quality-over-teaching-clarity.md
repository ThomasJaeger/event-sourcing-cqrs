# 0025. Production quality over teaching clarity

## Status

Accepted (May 2026)

## Context

The repository's foundational framing in CLAUDE.md held the codebase as
"a teaching artifact, not a production framework" whose readers "clone
it, run it, study it, and use it as a reference for their own systems.
They do not deploy it." The "What 'good' looks like" section that
followed reinforced the position: "When there is a choice between
idiomatic .NET and the book's pattern, the book's pattern wins," and
"Generic abstractions that hide the pattern are worse than concrete code
that shows it."

Two architectural decisions extended that framing into specific design
choices. ADR 0004 (self-contained event-store adapters) justified
accepted code duplication across adapters by appealing to pedagogical
transparency: "Each adapter reads end-to-end as a teaching artifact. …
The reference implementation's pedagogical purpose is served by this
transparency." ADR 0012 (process-manager type hierarchy) justified the
parallel `IProcessManagerEvent` / `IDomainEvent` hierarchy and the
conservative `GetUncommittedEvents` / `MarkCommitted` split on the same
grounds: "The reference implementation's pedagogical value depends on
showing the distinction cleanly rather than papering over it."

The framing collided with the actual use of the codebase. The
orchestrator adopts this reference implementation as the basis for
production services, with the SQL Server adapter slated to serve as the
foundation for at least one such service. Other readers do the same,
treating the codebase as a template for commercial systems rather than a
study artifact. Code with a teaching-friendly version that diverges from
what a production team would write was a defect waiting to surface, not a
pedagogical choice serving its readers.

Three pre-write defects in the Phase 7 Web host scaffolding made the
collision concrete: a registry composition that would have shipped
without the dispatch-boundary guard a production service requires, a
missing Razor framework imports file that would have failed under
TreatWarningsAsErrors with the project compiling only because of a
teaching-friendly relaxation, and a namespace mismatch between
code-defined and Razor-generated types. Each defect's smallest-diff
resolution was teaching-friendly; each defect's correct resolution was
production-grade. The pattern is not specific to those three; it
generalizes.

CLAUDE.md was reframed in commit `08644e0` to make production-grade
correctness, rigor, and operational hygiene non-negotiable across every
line of code in the repository, with the "Production quality is
non-negotiable" section naming the rule explicitly. This ADR records the
decision as a decision-record-weighted artifact, both for its own
durability and to govern the relationship between this rule and the two
prior ADRs whose justifications it reframes.

## Decision

Production-grade correctness, rigor, and operational hygiene govern every
line of code in this repository. There is no axis on which a
teaching-friendly shortcut wins. When the chapter prose depicts a
teaching-friendly version of a piece of code and the production version
diverges, the production version ships and the chapter prose updates to
depict it, tracked as a manuscript-reconciliation candidate (F-NNNN).

This decision applies across the codebase uniformly. It does not carve
out an axis on which teaching-friendly framing remains preferred.
Pedagogical transparency continues to be valuable as a goal where it does
not compete with production quality, but the priority is established and
explicit: production quality wins.

Concretely:

- Configuration validates at startup. Required dependencies throw on
  missing input with named exception types. No silent defaults.
- Failures surface as named exceptions at the boundary that owns them. A
  defect in one host surfaces in that host's logs, not as a downstream
  rejection from a host with no visibility into the originating cause.
- Cancellation propagates through async chains. No fire-and-forget. No
  swallowed cancellation tokens.
- Lifecycles are managed. `IAsyncDisposable` is awaited. Scoped services
  resolve in scopes. Singletons are stateless or guarded with explicit
  synchronization.
- Logging is structured. Secrets do not appear in logs or in exception
  messages that get logged. TLS is the default for cross-host transport.
- Error handling is intentional. `catch (Exception)` either rethrows,
  translates to a named exception type, or surfaces a documented failure
  mode. None of them swallow.
- Tests assert on real behavior production callers depend on. Integration
  tests exercise the wire format. Unit tests exercise the contract, not
  the incidental implementation.

The decision overrides any framing elsewhere in CLAUDE.md, in any ADR, in
any session log, or in any planning artifact that suggests teaching
clarity competes with production rigor. Where such framing exists in
artifacts predating this ADR, the artifact is treated as carrying the old
framing until the next change touching it reframes the wording; the
architectural rules that artifact carries remain in force in the
meantime, since they survive on production-quality reasoning.

Two ADRs are affected concretely: ADR 0004 (self-contained event-store
adapters) and ADR 0012 (process-manager type hierarchy). Both ADRs'
decisions stay accepted and in force. ADR 0004's self-contained-adapter
rule survives on production-quality reasoning (storage engines have
substantively different concurrency, transaction, and outbox semantics
that a shared abstraction would force into a lowest-common-denominator
shape; per-adapter independence reduces cross-adapter coordination cost
on real production changes). ADR 0012's parallel type-hierarchy decision
survives on production-quality reasoning (type-system enforcement of
meaningful behavioral differences over convention-based reliance prevents
the kind of conflation defect that surfaces only in production-load
conditions; the conservative `GetUncommittedEvents` / `MarkCommitted`
split prevents persist-failure data loss). The pedagogical-transparency
justifications in those ADRs (ADR 0004's "Each adapter reads end-to-end
as a teaching artifact" and "The reference implementation's pedagogical
purpose is served by this transparency"; ADR 0012's "The reference
implementation's pedagogical value depends on showing the distinction
cleanly rather than papering over it") are reframed onto
production-quality grounds. The reframe lands in those ADRs at the next
ADR-touching change to them, or at an explicit supersession ADR; this ADR
records that the reframe is pending and why.

No retroactive editing of decision-records is undertaken to chase the
reframe across the ADR corpus. The discipline lives in CLAUDE.md and in
this ADR; consumers of the prior ADRs read the justifications in light of
this one.

## Consequences

- Defect resolutions across the codebase favor the production-correct
  option over the smallest-diff option. The Phase 7 Web host
  scaffolding's three pre-write defects (a cross-assembly registry
  reachability issue, a missing Razor imports file, a namespace mismatch)
  all resolve toward production-correct shapes; the reframing of the first
  defect's resolution from drop-the-registry to relocate-the-registry
  traces directly to this ADR.

- Cluster boundaries do not bound the application of the rule. A defect
  surfaced in cluster N that has a production-quality resolution outside
  cluster N's stated scope gets resolved in cluster N anyway, with the
  cluster scope adjusted to absorb the work and the commit count adjusted
  accordingly. Cluster reflow happens for design-correctness reasons as
  well as for design-density reasons.

- Some refactoring follows from the reframe. The next session that
  touches ADR 0004 or ADR 0012 carries the wording-reframe as part of its
  scope. CommandTypeRegistry's placement under EventStore.Postgres (the
  production-quality boundary-failure issue) is the first refactor traced
  to this ADR; it lands in the cluster following this ADR's commit.

- The chapter prose carries the prior teaching-friendly framing in
  places. Phase 14 manuscript reconciliation absorbs the divergences as
  F-NNNN candidates. The F-0011 candidates accumulated through Cluster 2
  already document the same shape (Chapter 10's `ICommandBus` shape,
  Chapter 15's idempotency-key-on-command depiction, Chapter 15's
  per-query-endpoint depiction, the dispatched-via-CommandAcceptedResponse
  shape, Chapter 15's no-scope-per-dispatch depiction). Future candidates
  traced to this ADR join that list.

- The SQL Server adapter (Phase 2 scope), the KurrentDB adapter (Phase
  10), and the DynamoDB adapter (Phase 11) all land under this discipline
  from inception. The orchestrator's commercial-service adoption path (SQL
  Server adapter feeding at least one such service) is the first
  production consumer of the rule.

- Cross-host transport additions land with TLS by default. The Hosts.Web
  `IApiClient` against the Hosts.Api endpoints is the first cross-host
  transport; the production-quality discipline names TLS as the default
  for any such transport. Phase 8 SignalR additions, future
  event-streaming additions, and any cross-process integration carry the
  same default.

- Test coverage shifts toward contract-level assertions. Integration
  tests exercise the wire format; unit tests exercise the contract
  production callers depend on, not incidental implementation choices.

## Trigger for revisiting

- A reader or adopter surfaces a case where production-quality and
  teaching-clarity demands diverge in a way that the rule resolves toward
  production but the reader's adoption depends on the teaching-friendly
  version. The trigger is not adoption convenience; the trigger is a case
  where the production-quality version would be incorrect for a class of
  adopters that the repository should serve. Such a case has not been
  observed.

- An ADR that this one anticipates reframing (0004 or 0012) reaches the
  next-touch point and the reframing reveals that the underlying decision
  does not survive on production-quality reasoning. The decision would
  then need to be re-litigated under the new rule; the reframing is not
  automatic.

- A production-quality bullet in the Decision section proves overly
  restrictive at a concrete consumer (for example, a test scenario that
  requires a deliberately-swallowed exception for a reason that survives
  review). The bullet is refined or carved out by amendment, not retired
  wholesale.

- The repository's adoption pattern shifts such that production use cases
  dominate. If production adopters become the primary readership and the
  manuscript-side framing becomes the secondary concern, an amendment to
  CLAUDE.md may follow that further narrows the teaching-friendly framing
  in the repository.
