# TDD Working Rules: Event Sourcing & CQRS Reference Implementation

These rules govern how Test-Driven Development is applied in this repository. They extend,
and never override, the working rules in CLAUDE.md: propose before writing, stop and ask
before deviating, build green between steps, and the code wins over the manuscript. TDD is the default for
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
- **A characterization's non-vacuity is proven by a scratch break.** Behavior that already
  works has no RED to show. The fact passes the moment it compiles, and a fact that cannot
  fail proves nothing. Break the production path in the working tree so the fact fails, run
  it, and confirm it fails at its assertions rather than at arrangement or at compile. Then
  discard the break and re-run green against the restored tree, so the green verdict is
  against what ships rather than against the memory of a run. The break is reported in the
  turn with its exact shape and its verbatim failure, it never reaches the index, and the
  restored file is verified byte-identical before the commit. If the strict build turns the
  break into a compile error rather than a failing assertion, relax the scratch build enough
  to reach assertion level and say so. Ruled at the SQL Server dry-run branch, which was
  correct and unpinned: the break applied each pending migration inside the dry-run loop, and
  the fact failed on its write-nothing assertion with the reporting assertions still passing.
- **Minimal GREEN.** Implement the least code that turns this one test green. No
  speculative generality; no handling cases no test demands yet.
- **Test through behavior, not internals.** Drive aggregates through commands and observe
  emitted events / rejections. No reaching into private state. No coupling to details that
  refactoring should be free to change.
- **Don't mock what you don't own.** Never mock `DbConnection`, the KurrentDB client, or
  the AWS SDK. Verify adapters against the real backend via Testcontainers / LocalStack and
  the contract suite.
  - **The error-path carve-out.** A derived stand-in of a client owned by someone else is
    allowed under all five of these conditions, and the first one to fail takes the
    permission with it: it stands in only for an **error path the live engine cannot
    deterministically produce** (a cancellation aimed at one item index, a retry loop's
    exhaustion); the shape it returns is one a **spike measured against the live engine**,
    never one invented to suit the test; it is **named in the fact's header** as a
    stand-in, with the condition it satisfies; it **replaces no live-engine fact**, so
    whatever the engine really does stays pinned against Testcontainers or LocalStack; and
    the reach for it is **surfaced, not assumed** (flag, don't skip). Prefer extracting the
    decision into a pure seam and pinning that directly, which needs no stand-in at all:
    the DynamoDB adapter's cancellation translation went that way, and only the loop's
    exhaustion kept a client. A stand-in that pins what a backend does, rather than what
    the adapter does about it, is outside the carve-out and is the thing this rule exists
    to forbid.
- **One assertion of behavior per test** (multiple physical asserts checking one behavior
  are fine). Name tests as specifications.
- **Test reach: the preference order.** Reaching a behavior a test cannot otherwise see is a
  recurring pressure, and the answer is an order, not a judgment call. Take the first option
  that works and say why the ones above it did not:
  1. **An existing injected seam.** A constructor parameter or a hand-built harness the tests
     already supply. Costs nothing and adds no production surface. The DynamoDB dispatcher's
     wake facts decorate `IAmazonDynamoDBStreams` and `IEventStore` this way, because the
     harness already hands both to the service by hand.
  2. **An internal seam under `InternalsVisibleTo`.** Extracting a decision into a type the
     test assembly can already see. Prefer a **pure** seam: it needs no stand-in and pins
     directly. The DynamoDB adapter's cancellation translation went this way, and the facts
     that had needed a client to reach it now call a function.
  3. **Inert production widening, named as test-reach.** A setting or visibility change that
     no shipping caller uses, taken only when the behavior is otherwise unpinnable, and
     labelled in the code as existing for reach. `DynamoDbEventStoreOptions.MaxAppendAttempts`
     is settable because 256 real losses is a load test rather than a fact; `CancellationVerdict`
     is public because xUnit needs a public test class and a public method cannot take an
     internal parameter. Both say so where they sit. "Inert" is the whole condition: widening
     that changes what production does is not reach, it is a design change wearing a test's
     clothes.
  4. **Nothing.** Some behavior is not worth its seam. Say so in the fact's header and pin the
     property that makes the behavior true instead of the scenario that would catch it
     failing. The dispatcher's acquire-before-drain ordering is pinned as an ordering rather
     than raced, because racing it would need option 5.
  **Never a timing or delay seam in a hot path.** A `Task.Delay` hook, a settable interval, or
  a callback wedged between two awaits so a test can slip inside the window: these buy a fact
  by making the shipped loop slower, more complex, and differently timed than the one that
  runs in production, which is the thing under test. A window narrow enough to need one is a
  window to close by construction and pin by its invariant.

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
