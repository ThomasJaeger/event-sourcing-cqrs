# 0006. Money is a Shared Kernel Between Sales and Billing

## Status

Accepted (May 2026)

## Context

`Money` ships at `src/Domain/SharedKernel/Money.cs` as a `sealed record (decimal Amount, string Currency)` with `+` and `-` operators that throw `InvalidOperationException` on currency mismatch. A `Money.Zero` static carries an empty-string currency as the additive identity; the `+` operator has special-case handling so currency-less identity participates in aggregation without forcing every starting accumulator to know a currency up front. The `-` operator has no such special case. Sales has used `Money` since Phase 3 in event payloads (`OrderPlaced.Total`, `OrderLineAdded.UnitPrice`), in the `OrderLine` value object (`UnitPrice`, `Subtotal`), in the `Order` aggregate's `Total` computation, and in the `OrderListRow` read model. There are no `Money` extension methods, no per-context `Money` subtypes, and no second `Money` definition anywhere in the codebase.

Phase 4 introduces the Billing context with a `Payment` aggregate whose four events (`PaymentAuthorized`, `PaymentCaptured`, `PaymentRefunded`, `PaymentVoided`) all carry monetary amounts. The decision is whether Billing reuses Sales' `Money` type (the shared-kernel pattern from Ch 7) or defines its own.

## Decision

Phase 4's Billing context uses `Money` from `Domain/SharedKernel/`. No `Billing.Money` type is introduced. Sales and Billing share the same invariants: currency-tagged decimal amounts, no cross-currency arithmetic without explicit conversion, `Money.Zero` as the currency-less additive identity for aggregations.

If future currency-aware operations emerge that Billing needs and Sales does not, they are addressed as a separate decision at that point; see Trigger-for-revisiting for the conditions and the first-cut response.

Pattern specification: see ADR 0009 (Money implements Fowler's pattern from PoEAA, including typed precision-aware Currency and Allocate methods). ADR 0006 specifies that Money is shared across contexts; ADR 0009 specifies the shape of that shared Money.

## Consequences

- Sales and Billing share invariant ownership of `Money`. A change to `Money` requires considering both contexts' needs. Phase 4 ships no such change.
- Payment events carry `Money` amounts that are never currency-less. The `Money.Zero` identity asymmetry (additive identity exists, subtractive identity does not) does not affect Payment in practice; Payment does not aggregate monetary values across an empty starting accumulator.
- Read-side stores serializing Payment events to PostgreSQL follow the same mapping convention Sales uses (`OrderListRow.Total` maps to `total_amount` + `total_currency` columns). The convention is established in Phase 6 when Payment projections land.
- Ch 7's shared-kernel pattern is depicted concretely by `Money` across Sales and Billing. The reference implementation has a worked example of the pattern the manuscript names in the abstract.

## Trigger for revisiting

The decision to share `Money` between Sales and Billing is reversible. Conditions that would justify reopening it:

- A regulatory or domain invariant emerges that diverges between Sales' and Billing's monetary semantics — for example, Billing must track exchange-rate-effective-date on every captured amount in a way Sales does not. The point of divergence becomes the test: if a domain service operating on `Money` captures the new invariant cleanly, the shared type holds; if the new invariant lives on the type itself, the contexts split.
- Multi-currency arithmetic enters scope (an order placed in EUR, paid in USD via conversion). The first cut is a `CurrencyConversionService` returning a new `Money` in the target currency. If that service grows complexity that argues for per-context `Money` types (Sales' EUR view, Billing's USD view, both linked by conversion metadata), the split is reconsidered.
- A Payment-specific invariant emerges that `Money` cannot represent without making Sales carry the cost — for example, a precision-context requirement that Sales' `decimal` does not need. The cost-benefit between forcing Sales to absorb the change and splitting the type becomes the deciding factor.

A type split, if undertaken, is straightforward in mechanics (introduce `Billing.Money` or `MonetaryAmount`, update Payment events and command handlers, write a mapping between the two types at the boundary if Sales and Billing exchange monetary values) but expensive in surface area (every Payment-touching adapter, projection, and test follows). The wrapper-refactor cost note from ADR 0005's trigger section applies analogously.
