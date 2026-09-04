# 0005. Raw Guid for Aggregate and Cross-Context Identifiers

## Status

Accepted (May 2026)

## Context

The Phase 4 planning conversation locked a decision under the heading "Shared Kernel identifier types," assuming the reference implementation already shipped `OrderId` and `CustomerId` as strongly-typed value-object wrappers around `Guid`, living in `src/Domain/SharedKernel/` alongside `Money` and `Address`. The plan's role for ADR 0005 was to record the existing shape and forward it to Phase 4's three new aggregates.

Commit 2's pre-flight against disk surfaced that the assumption was wrong. No `OrderId` or `CustomerId` type exists in the codebase. The shape, end-to-end:

- `AggregateRoot.Id` is `public Guid Id { get; protected set; }`.
- All seven Sales events declare `Guid OrderId`, `Guid CustomerId`, `Guid LineId`.
- `Order.cs` carries `private Guid _customerId;`. Command method signatures take `Guid lineId, Guid issuedByUserId`.
- All seven Application command records (`PlaceOrder`, `DraftOrder`, `AddOrderLine`, `RemoveOrderLine`, `SetOrderShippingAddress`, `ShipOrder`, `CancelOrder`) declare `Guid` for ID fields.
- `OrderListRow` (read-side row, just relocated in commit 1) declares `Guid OrderId, Guid CustomerId`.
- `EventMetadata.AggregateId` and `EventEnvelope.AggregateId` are `Guid`.
- All test fixtures declare `private static readonly Guid OrderId = Guid.Parse(...)`.

The manuscript was checked against this surfacing. Ch 9 (the chapter that carries the authoritative Order aggregate code block) declares `private Guid _customerId;` explicitly. Ch 7 advocates value-object wrappers as a general DDD pattern, naming `EmailAddress, Percentage, CustomerId` as examples of primitives that could be promoted, but does not claim the reference implementation applies the pattern. Ch 9 and disk agree; Ch 7's prose does not claim otherwise.

Phase 4 introduces three new aggregates (Inventory, Shipment, Payment) whose events will carry `OrderId` and `CustomerId` references across bounded-context boundaries. A decision is required now on whether Phase 4 ships the existing raw-`Guid` shape consistently, introduces typed wrappers (forcing a refactor across Sales code, AggregateRoot, command records, tests, EventMetadata, and the infrastructure adapters that pass IDs through to storage), or accepts a split shape (Sales raw Guid, Fulfillment and Billing typed). The split shape is rejected: the inconsistency would confuse readers, fragment the existing shape across exactly the boundary Phase 4 is teaching about, and force the eventual reconciliation to pick one side anyway.

## Decision

Cross-context identifiers in this reference implementation travel by raw `Guid`. The rule applies to `OrderId`, `CustomerId`, and by precedent to any future cross-context identifier that another bounded context refers to by reference rather than by definition. The sole exception is TenantId, the tenant-isolation discriminator, carved out as a security-justified typed identifier by ADR 0029; the raw-Guid rule above continues to govern every other cross-context identifier, including ActorId.

Concretely for Phase 4: Inventory, Shipment, and Payment events declare `Guid OrderId, Guid CustomerId` (and `Guid InventoryId`, `Guid ShipmentId`, `Guid PaymentId` for their own aggregate IDs). Command records and handlers follow the same shape. `AggregateRoot.Id` stays `Guid`; no generic `AggregateRoot<TId>` introduced.

Within-aggregate identifiers (such as `LineId` inside `OrderLine`) also use raw `Guid` in the current codebase, by precedent rather than by this ADR's mandate. A future session could legitimately decide to wrap a within-aggregate identifier without reversing the cross-context rule above.

## Consequences

- Phase 4 events ship consistent with Sales: `Guid OrderId, Guid CustomerId` in every payload that carries those references. No special-case handling in Inventory, Shipment, or Payment.
- `AggregateRoot.Id` and `EventMetadata.AggregateId` stay `Guid`. No generic introduced. No per-aggregate typed Id accessor pattern emerges.
- Type confusion at compile time (passing a `CustomerId`-shaped `Guid` where an `OrderId` was expected) remains possible. Domain validators and command-handler tests are the safety net, not the type system.
- The codebase stays readable as a Ch 9-style study text. Readers comparing the book's Order aggregate code block to the repo see the same `Guid` shape. The cluster-12 manuscript reconciliation that normalized `OrderPlaced` to 4-arg shape (`OrderId, CustomerId, Money Total, PlacedUtc`, all primitive or value-typed) survives without further reconciliation.
- Ch 7's typed-wrapper prose (line 604 in the current extract) remains accurate as general DDD advocacy. It does not claim this reference implementation uses the pattern; the absence of the pattern in the code is now made explicit by this ADR and can be cross-linked from Ch 7 if a future manuscript pass chooses to.
- The pedagogical cost is real: a reader who finishes Ch 7 wanting to see a typed-wrapper example will not find one in this reference implementation. The book may want to point that reader to an external resource or a non-canonical pedagogical snippet if Ch 7 is reworked to be explicit.

## Trigger for revisiting

The decision to keep raw `Guid` is reversible. Conditions that would justify reopening it:

- A domain bug whose root cause is identifier confusion (a `CustomerId`-shaped `Guid` passed where an `OrderId` was expected, surviving validators and tests). The bug's existence would shift the cost-benefit toward typed wrappers.
- A Phase 15 type-safety pass that takes wrappers as part of a broader refactor (for example, alongside the snapshot-versioning work). Bundling a wrapper migration into a larger type-safety session amortizes the cross-codebase churn.
- A strong manuscript-reconciliation argument that typed wrappers should ship for pedagogical reasons (Ch 7 reworked to depict the pattern in worked code rather than as abstract advocacy). That would be a manuscript decision that flows back into the code.

A wrapper refactor, if undertaken, is its own session. The work touches `AggregateRoot` (decide between generic `AggregateRoot<TId>` or per-aggregate typed accessors over a `Guid` base), all event payloads, all command records, all handlers, all tests, `EventMetadata`/`EventEnvelope` decisions, repository signatures, and infrastructure adapter boundaries. It does not fit as a sub-commit of an unrelated phase.
