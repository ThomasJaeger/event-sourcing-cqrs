# 0009. Money Implements Fowler's Pattern

## Status

Accepted (May 2026)

## Context

ADR 0006 records that `Money` is a shared kernel between Sales and Billing. The shape of that shared `Money` is the present decision.

The pre-Phase-4 `Money` shipped at `src/Domain/SharedKernel/Money.cs` as a `sealed record (decimal Amount, string Currency)` with `+` and `-` operators that threw `InvalidOperationException` on currency mismatch. A `Money.Zero` static carried an empty-string currency as an additive-identity hack so aggregation against an empty accumulator did not need an explicit starting currency. No multiplication, no comparison operators, no `IsNegative`, no `IsZero`, no allocation methods, no concept of currency precision.

Phase 4 ships Payment, which carries monetary amounts through authorization, capture, refund, and void events. Future-phase work (partial captures and partial refunds in Phase 15; the Phase 5 process manager's payment-and-inventory compensation branches) needs `Money` operations the pre-Phase-4 shape did not support: dividing an amount across parts with correct minor-unit rounding, comparing amounts, and reasoning about negative versus zero balances. The empty-string-Currency hack also leaked into the type's invariants in a way that could not survive a typed-currency model.

Martin Fowler's `Money` pattern from *Patterns of Enterprise Application Architecture* covers this surface: a typed `Currency`, currency-aware operations that throw on mismatch, allocation that distributes minor-unit remainders deterministically, comparison operators alongside arithmetic, and `IsNegative` / `IsZero` predicates that read like domain vocabulary.

The reference implementation is leading the manuscript on this decision. Chapters 7 and 9 currently depict a `Money` shape closer to the pre-Phase-4 record. The divergence is recorded as Track A flags for a Phase 17 manuscript reconciliation pass.

## Decision

`Money` implements Fowler's pattern from PoEAA. The shape:

- `Money` is `sealed record (decimal Amount, Currency Currency)`. `Currency` is a typed value object, not a string.
- `Currency(string Code)` validates ISO-4217 format (three uppercase ASCII letters) in the constructor and throws `DomainException` on invalid input. Static accessors `Currency.USD`, `Currency.EUR`, `Currency.GBP`, `Currency.JPY` exist alongside the primary constructor. No `Parse` or `TryParse`; the constructor is the only validating entry point.
- `Currency.DecimalPlaces` reads from an internal precision table at access time. Known currencies (USD, EUR, GBP, JPY, BHD) carry table values; unknown valid-format codes default to 2. `Currency`'s only state field is `Code`, so equality discriminates on code alone.
- `Money` exposes `+`, `-`, `*` (scalar multiplication by `decimal`), `<`, `>`, `<=`, `>=`, and record-default `==` / `!=`. Properties `IsNegative` and `IsZero` classify the amount.
- `Money.Zero(Currency)` is the only zero factory. No bare `Money.Zero` property.
- `Money * decimal` rounds to `Currency.DecimalPlaces` using banker's rounding (`MidpointRounding.ToEven`).
- `Money.Allocate(int n)` divides the amount into `n` parts; rounding remainders push to leading elements so the parts sum to the original.
- `Money.Allocate(int[] ratios)` divides proportionally across ratios with the same remainder-distribution rule.
- All cross-currency operations throw `DomainException` via a private `EnsureSameCurrency` helper.

`Money` does not know about exchange rates. Any future currency-conversion concern stays outside the type.

## Consequences

- Multi-part allocations distribute remainders deterministically. The Phase 5 process manager's partial-compensation branches and the Phase 15 partial-capture and partial-refund work inherit a `Money` type that handles their math correctly without per-caller minor-unit-rounding ceremony.
- Currency mismatch becomes a typed catch surface. The shift from `string Currency` to `Currency Currency` moves a class of "USD vs usd" bugs out of runtime validators and into the compiler's reach.
- Scalar multiplication rounds at every operation site. `OrderLine.Subtotal` switches from raw decimal multiplication to `UnitPrice * Quantity` and rounds at the line level. For prices already at currency precision (the typical case) the result is unchanged. For prices with sub-currency-unit precision, the rounding happens earlier than the previous code path.
- The empty-string-Currency identity hack disappears. `Order.Total` for an empty order returns `Money.Zero(Currency.USD)`. The empty-order invariant lives at `Order.Place` time; transient empty-Total state is bounded to the draft window.
- The exception type on currency mismatch changes from `InvalidOperationException` to `DomainException`, aligning `Money` with the project's invariant-violation convention. No existing test asserts on the prior exception type.
- `Money.Allocate` and `Money * decimal` fix the minor-unit rounding policy via the precision table. A currency operationally important to the system that is not in the precision table gets a 2-decimal default, which is wrong for currencies like JPY (0) or BHD (3). The default behavior is safe for the common case and visible for the uncommon case via the trigger conditions below.
- Ch 7's `Money` code block and Ch 9's BankAccount code block diverge from the shipped `Money`. Track A flags A through E capture the specific reconciliation surface.

## Trigger for revisiting

The decision to implement Fowler's pattern is reversible. Conditions that would justify reopening it:

- A currency the system operationally depends on is missing from the precision table, and the 2-decimal default produces wrong arithmetic in production-shaped tests. The first cut is to add the currency to the table; if the system needs many such currencies, the table mechanism gets revisited.
- An exchange-rate-effective-date concern surfaces that pushes for `Money` to know about rates. ADR 0009 explicitly forbids `Money` carrying rate data; the trigger is the scenario where that prohibition becomes load-bearing-painful and the cost-benefit favors integrating rate awareness into the type rather than into a separate service. ADR 0006's revisiting trigger anticipates this same scenario at the kernel-sharing layer.
- Performance pressure on `Allocate` at scale forces a representation change (for example, moving from `decimal` to a minor-unit integer representation). The trigger is observed allocation overhead in a profiler-driven case, not anticipated micro-cost.
- A multi-precision currency requirement that the access-time precision lookup cannot accommodate cleanly. The current shape assumes precision is a function of currency code alone; if a regulatory or domain rule emerges that ties precision to context (a tax-calculation context using 4 places, a customer-facing display using 2), the assumption gets revisited.

A pattern change, if undertaken, touches every site that constructs `Money` or invokes its operations. The wrapper-refactor cost note from ADR 0005's trigger section applies analogously.
