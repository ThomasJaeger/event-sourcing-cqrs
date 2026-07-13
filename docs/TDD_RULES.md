# TDD Working Rules: Event Sourcing & CQRS Reference Implementation

These rules govern how Claude.ai (analysis/directives) and Claude Code (execution) apply
Test-Driven Development to this repository. They extend, and never override, the existing
working rules (propose before writing, stop and ask before deviating, build green between
steps, log cross-track flags, implementation wins over manuscript). TDD is the default for
behavior. It is not a blanket mandate for every file.

---

## 1. Scope: where TDD applies

**Mandatory (RED before any production code):**
- **Domain layer.** Aggregates, value objects, domain events, invariants. Every
  command → events path and every command → rejection path.
- **Application layer.** Command handlers, projection / read-model builders,
  process managers (sagas), idempotency and concurrency handling.
- **The `IEventStore` / `IEventStoreRepository<TAggregate>` contract.** Behavior defined
  once via a shared contract suite (see §5).

**Spike-then-stabilize (discover first, then pin with tests before "done"):**
- Adapter internals where the backend's real contract is unknown: hand-rolled
  PostgreSQL (Npgsql exceptions, SQLSTATE for concurrency), hand-rolled SQL Server
  (Microsoft.Data.SqlClient exceptions, error numbers for concurrency), KurrentDB client
  semantics, DynamoDB-via-LocalStack quirks. Spike against the *live* backend to learn the
  contract, then make the adapter pass the shared contract suite. **The spike is throwaway;
  it is not the deliverable.**

**Optional / judgment (do not force TDD):**
- Pure wiring (DI registration, `Program.cs`, config), trivial DTO mapping, CLI argument
  parsing, migration SQL already exercised by an integration test. Test where it earns its
  keep; no ceremony tests for getters.

**Flag, don't skip.** When a unit looks like spike-then-test or optional, say so and why,
then wait. Never silently decide to skip the RED step.

---

## 2. The cycle: strict Red → Green → Refactor

One behavior at a time. The three laws hold:
1. No production code until a failing test demands it.
2. Write only enough of a test to fail (a compile error counts as a failure).
3. Write only enough production code to pass the one failing test.

- Start each unit by proposing a **test list** (the behaviors to cover) for approval, which
  satisfies "propose before writing." Work the list one item at a time.
- **Triangulate** when a single example would let a fake implementation pass. Use the
  "obvious implementation" shortcut only when the code is obvious.
- **Refactor only on green**, production and test code both. The bar is green at every
  step boundary; RED exists only transiently for the test under construction.

---

## 3. Agent enforcement: anti-theater

These exist because an agent will otherwise write the test and the implementation in one
breath and present a green bar that never went red.

- **Separate the steps.** Writing the failing test and writing the implementation are two
  distinct turns or commits, never the same diff.
- **Show the RED.** Run the new test and paste the actual failure output *before* writing
  production code. Confirm it fails **for the intended reason** (the asserted behavior is
  missing), not because it didn't compile, threw an unrelated error, or the test is wrong.
- **Minimal GREEN.** Implement the least code that turns this one test green. No
  speculative generality; no handling cases no test demands yet.
- **Test through behavior, not internals.** Drive aggregates through commands and observe
  emitted events / rejections. No reaching into private state. No coupling to details that
  refactoring should be free to change.
- **Don't mock what you don't own.** Never mock `DbConnection`, the KurrentDB client, or
  the AWS SDK. Verify adapters against the real backend via Testcontainers / LocalStack and
  the contract suite.
- **One assertion of behavior per test** (multiple physical asserts checking one behavior
  are fine). Name tests as specifications.

---

## 4. Event-sourcing test patterns (e-commerce domain)

Express aggregate tests as **Given-When-Then**, which is also how they read as executable
specs in the book.

- **Aggregate behavior:**
  `Given(prior events) → When(command) → Then(expected new events)` or
  `Then(rejected with reason)`.
  Order aggregate examples:
  - Given an empty stream, when `PlaceOrder`, then `OrderPlaced`.
  - Given `OrderPlaced`, when `AddLineItem`, then `LineItemAdded`.
  - Given a submitted order, when `AddLineItem`, then rejected (invalid state).
  - Given `OrderPlaced` + `OrderPaid`, when `Pay` again, then rejected / no-op (idempotent).
- **Invariants:** assert the rule, not the mechanism. Order total can't go negative;
  can't ship an unpaid order; can't add items to a cancelled order.
- **Projections / read models:** `Given(event stream) → Then(expected read model)`. Fold
  events, assert the materialized view (`OrderSummary`, `CustomerOrderHistory`,
  `InventoryOnHand`). Replaying the same events yields the same state (idempotent).
- **Process managers / sagas:** `Given(prior events) → When(triggering event) →
  Then(commands dispatched)`. E.g. given `OrderPaid`, when `InventoryReserved`, then
  dispatch `ShipOrder`.
- **Concurrency at the domain/app level:** an expected-version mismatch surfaces as a
  conflict the handler reacts to; assert the retry / reject behavior, not the storage detail.

Maintain a small reusable harness (a `Specification` / `AggregateTestFixture` style helper)
so these read uniformly across chapters.

---

## 5. The `IEventStore` contract suite: one suite, four adapters

Write a single abstract / parameterized suite against `IEventStore` (and
`IEventStoreRepository<TAggregate>`) and make PostgreSQL, SQL Server, KurrentDB, and
DynamoDB/LocalStack each pass the **identical** suite. This is the test-first surface that
matters for adapters even when their internals are spiked.

Behaviors the suite pins down:
- Append then read returns the same events, in order, from version 0.
- `ReadStreamAsync` from an arbitrary version returns the tail correctly.
- Appending with a stale expected version raises the concurrency-conflict contract, a
  single store-agnostic exception type; adapters translate native errors into it.
- Idempotent append by event id / dedup key where the design promises it.
- Global ordering by `global_position` is monotonic in commit order and gap-tolerant:
  gaps come only from rolled-back appends and are permanent, never transient (ADR 0044).
- Reading a non-existent stream behaves per contract (empty vs. throws: pick one, pin it).

An adapter's internals may be spiked first; **"done" = green against this suite on the real
backend** via Testcontainers / LocalStack.

---

## 6. Integration with existing working rules

- **Propose before writing** → propose the **test list** first.
- **Build green between steps** → green at every step boundary; RED only transient.
- **Implementation wins; log a cross-track flag** → if a test would encode manuscript
  behavior the implementation must contradict, the implementation wins, the test is written
  to the implemented behavior, and a cross-track flag is logged as an F-NNNN candidate,
  subject to the three carve-outs (pedagogical anchors, public-API commitments,
  multi-chapter ripple changes).
- **Stop and ask before deviating** → if test-first proves a poor fit mid-unit, stop and
  flag; do not abandon the RED step silently.

---

## 7. Definition of Done (per unit of behavior)

- [ ] A failing test existed and was shown failing for the right reason.
- [ ] Minimal production code drove it green.
- [ ] Refactored on green; whole suite green.
- [ ] Adapters: green against the shared contract suite on the real backend.
- [ ] Tests read as specifications (Given-When-Then names); no coupling to internals.
- [ ] Any scope deviation (spike / optional / skip) was flagged and approved, not assumed.
