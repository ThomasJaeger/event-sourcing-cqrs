# 0007. Line Types Stay Per Context

## Status

Accepted (May 2026)

## Context

`OrderLine` ships at `src/Domain/Sales/OrderLine.cs` as a `sealed class` (reference equality, entity identity discipline) with `LineId` as the identity field plus `Sku`, `Quantity`, `UnitPrice`, and a computed `Subtotal` derived from unit price and quantity. The `Order` aggregate holds `List<OrderLine>`, looks lines up by `LineId == ...`, and reconstructs them in Apply by constructing fresh instances on every replay. `OrderLineAdded` and `OrderLineRemoved` events both carry `LineId`. The shape is consistent: entity with stable identity, value fields, pricing-derived computation.

Phase 4 introduces two new contexts that conceptually share the "line" word but model it differently:

- **Inventory** (Fulfillment context). A `ReservationLine` records that a specific quantity of a specific SKU has been reserved for a specific Order's line (correlated via `LineId`). The reservation cares about `Sku` and `Quantity` (does the warehouse have enough?) and the originating `LineId` (to release the right reservation on cancellation). The reservation does not care about `UnitPrice` — pricing is not an Inventory concern.
- **Shipment** (Fulfillment context). A `ShipmentLine` records what physical contents are scheduled to ship in a package. The shipment cares about `Sku`, `Quantity`, and potentially package-internal metadata (carton index, weight, dimensions if those land). The shipment does not care about `UnitPrice` — pricing is not a Shipment concern.

A naive shared-kernel approach would put a single `Line` type in `SharedKernel` (or an `ILine` interface in `Domain.Abstractions`), carrying fields for every context's needs. That approach fails twice: a reader of the type cannot tell which fields are load-bearing for which context, and adding a Shipment-specific field (carton index) silently changes the shape of Sales' lines and Inventory's reservation lines, neither of which need it.

The decision is whether the three line concepts share a type, share an abstract base, or live as fully independent types per context.

## Decision

The three line concepts are independent types per context. Sales owns `OrderLine` (already shipped). Fulfillment owns `ReservationLine` and `ShipmentLine` (land in commits 6 and 12 of this session). No shared `Line` base class, no `ILine` interface in `Domain.Abstractions`, no SharedKernel `Line` record.

The pattern is Customer-Supplier from Ch 7. Sales is the supplier of `OrderLineAdded` events; Fulfillment is a customer of those events when it needs to materialize a reservation or a shipment line. The translation between Sales' line shape and Fulfillment's line shapes is the responsibility of the boundary code that consumes upstream events and dispatches downstream commands.

Phase 4 ships the type definitions. Phase 5 ships the translation mechanism. Phase 5 owns the question of exactly where the translation code lives (the natural candidate is the `OrderFulfillmentProcessManager` itself, but a translator service or mapping layer is also viable). What this ADR commits to is the location of responsibility: translation is a process-manager-side concern, not a shared-kernel concern. The translation cost is concentrated at the boundary rather than smeared across every line-producing or line-consuming type.

## Consequences

**File layout after Phase 4.**

- `src/Domain/Sales/OrderLine.cs` — unchanged. Sales' entity with `LineId`, `Sku`, `Quantity`, `UnitPrice`, derived `Subtotal`.
- `src/Domain/Fulfillment/ReservationLine.cs` — lands in commit 6 as part of Inventory scaffolding. Carries `LineId`, `Sku`, `Quantity`. No pricing.
- `src/Domain/Fulfillment/ShipmentLine.cs` — lands in commit 12 as part of Shipment scaffolding. Carries `LineId`, `Sku`, `Quantity`, plus any package-specific fields the implementation discovers. No pricing.
- No `src/Domain.Abstractions/ILine.cs`, no `src/Domain/SharedKernel/Line.cs`, no abstract base class introduced.

**Pattern visibility.**

- Ch 7's Customer-Supplier pattern is depicted concretely by the OrderLine → ReservationLine + ShipmentLine relationship. The reference implementation has a worked example of the pattern the manuscript names in the abstract.
- The translation seam is named (lives on the process-manager side of the boundary) before the code lands. Commit 22's cross-context vocabulary documentation cites this ADR at the boundary sites.

**Phase 5 inheritance.**

- The Phase 5 planning conversation inherits this architectural commitment, not an open question. The process manager design assumes per-context line types and accepts translation as a process-manager-side concern, not a shared-kernel concern.
- The exact mechanism (process manager method, translator service, mapping layer) is Phase 5's design choice. This ADR locks the location of responsibility, not the implementation shape.
- If a fourth line concept emerges in a later phase (returns, RMA, audit trail), it follows the same pattern: new context, new line type, translation at the boundary.

**Inheritance of identity discipline.**

- `LineId` carries through all three line types as the identity key. A reservation refers back to which Sales line it was for via `LineId`; a shipment line refers back to which Sales line it carries. The identity discipline propagates through the translation without requiring a shared base.
- The shared `LineId` field across the three types is intentional. It is not the same as a shared base type; it is a shared identifier that lets the contexts correlate without coupling.

## Trigger for revisiting

The decision to keep three independent line types is reversible. Conditions that would justify reopening it:

- A discovered field overlap between the three line types so large that the per-context split looks like duplication rather than separation. Unlikely given the shape difference already documented (pricing is Sales-only, package fields would be Shipment-only). Worth naming because future phases might surface fields none of the current sketches anticipate.
- A discovered need for shared invariants across line types that cannot be expressed without a shared base. Unlikely; current invariants are per-context by construction (Sales' line invariant is "non-negative quantity, non-empty SKU, positive unit price"; Inventory's reservation invariant is "non-negative quantity, SKU exists in catalog"; Shipment's invariant is "non-negative quantity, line fits the package"). The invariants share grammar without sharing semantics.
- **Phase 5 implementation surfaces that the translation cost in the process manager is high enough to argue for a shared kernel after all.** This is the trigger most likely to actually fire. Translation code that turns out to be repetitive, error-prone, or hard to test would shift the cost-benefit toward a shared base type. The Phase 5 execution session should treat translation cost as a quantity worth measuring and surfacing if it grows.

A line-type unification, if undertaken, has high cost (every event, every projection, every test that touches lines follows; the process manager translation code disappears but is replaced by per-call field-selection ceremony elsewhere). The wrapper-refactor cost note from ADR 0005's trigger section applies analogously — undertaken as its own session, not as a sub-commit.
