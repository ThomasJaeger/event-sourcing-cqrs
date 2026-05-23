# Cross-Context Vocabulary

This document names what crosses each bounded-context boundary in the reference implementation, and what does not. It is the companion to the four context-mapping ADRs (0005, 0006, 0007, 0008) and to Chapter 7's context-mapping section in the book. Readers coming to the code first should find here the answer to "why does Fulfillment carry an `OrderId` field but never reference Sales' `Order` type?"

## Purpose

The reference implementation has four bounded contexts that share information without sharing types. The shape is opinionated: identifiers travel as raw `Guid`, monetary amounts travel through a small shared kernel, and every context defines its own line types and aggregate state. The substantive decisions live in ADRs 0005 through 0008. This document gathers them into one place so a reader can see the cross-cutting picture in one read.

The dominant pattern is Customer-Supplier from Chapter 7. Sales publishes events. Fulfillment and Billing consume them. The downstream contexts do not import Sales' types; they translate at the boundary. The boundary translation lives in the process managers, not in a shared kernel.

## The four contexts

**Sales** (`src/Domain/Sales/`). Owns `Order` and `OrderLine`. Publishes `OrderDrafted`, `OrderLineAdded`, `OrderLineRemoved`, `ShippingAddressSet`, `OrderPlaced`, `OrderShipped`, `OrderCancelled`, `OrderCompleted`. Sales is the source of order identity (`OrderId`), customer identity (`CustomerId`), and per-order pricing (`Money UnitPrice`).

**Fulfillment** (`src/Domain/Fulfillment/`). Owns `Inventory` and `Shipment`. Publishes `InventoryCreated`, `InventoryAdjusted`, `InventoryReserved`, `InventoryReleased`, `ShipmentScheduled`, `ShipmentDispatched`, `ShipmentDelivered`, `ShipmentReturned`. Fulfillment cares about SKUs and quantities. It does not care about pricing.

**Billing** (`src/Domain/Billing/`). Owns `Payment`. Publishes `PaymentAuthorized`, `PaymentCaptured`, `PaymentRefunded`, `PaymentVoided`. Billing cares about amounts and payment-method references. It does not care about line items or shipping.

**Customer Support**. No own aggregates. Reads from the projections the other three contexts' events feed: the four Phase 6 read models (`OrderListProjection`, `OrderDetailProjection`, `CustomerSummaryProjection`, `InventoryDashboardProjection`) plus the two Session-0009 cross-PM lookups (`SkuToInventoryId`, `OrderIdToPaymentId`). Its model is a read-only composite view; it adds no write-side dependency.

## The shared kernel

`src/Domain/SharedKernel/` holds three types shared by multiple contexts:

- **`Money`** (Sales and Billing). Fowler's pattern from PoEAA: typed `Currency`, arithmetic operators that throw on currency mismatch, allocation methods. See ADR 0006 (the cross-context sharing decision) and ADR 0009 (the Fowler-pattern shape).
- **`Currency`** (used through `Money`). Typed value object with ISO-4217 format validation and access-time precision lookup. See ADR 0009.
- **`Address`** (Sales and Fulfillment). Simple value record. Sales stores it on `Order` after `ShippingAddressSet`; Fulfillment carries it on `ShipmentScheduled` as the delivery destination.

What is **not** in the shared kernel: identifier types. `OrderId`, `CustomerId`, `LineId`, `PaymentId`, `ShipmentId`, `InventoryId` all travel as raw `Guid`. Chapter 7 lists "common identifier types" as a typical Shared Kernel candidate; this codebase deliberately does not promote IDs to typed wrappers. See ADR 0005 for the reasoning.

The shared kernel is deliberately small. Adding to it requires the sharing contexts to agree. The pattern is the one Chapter 7 names: small and stable, jointly owned.

## The boundary crossings

This section names what crosses each pair of contexts. The pattern is Customer-Supplier in every case where the implementation ships a real boundary.

### Sales to Fulfillment

What crosses: `OrderId` (raw `Guid`), `LineId` (raw `Guid`), `Sku` (string), `Quantity` (int), `Address` (shared kernel).

How: Fulfillment events carry `OrderId` and `LineId` as raw `Guid` fields. Examples: `InventoryReserved(InventoryId, OrderId, LineId, Sku, Quantity, ReservedUtc)`, `ShipmentScheduled(ShipmentId, OrderId, Destination, Lines, ScheduledUtc)`. Fulfillment does not reference `Sales.Order` or `Sales.OrderLine` types directly. Verified on disk: `grep -rn "using EventSourcingCqrs.Domain.Sales" src/Domain/Fulfillment/` returns zero hits.

Pattern: Customer-Supplier (Chapter 7). `OrderFulfillmentProcessManager` is the translator. It observes `OrderPlaced`, loads the `Order` aggregate for its lines, and on `PaymentAuthorized` fans out per line: it consults the `SkuToInventoryId` read model to translate each `Sku` into a Fulfillment-side `InventoryId`, dispatches `ReserveInventory` per line, then `ScheduleShipment` once every line reserves. The translation is the boundary work, and it lives in the process manager rather than in a shared type.

See ADR 0005 (raw Guid for cross-context IDs) and ADR 0007 (per-context line types).

### Sales to Billing

What crosses: `OrderId` (raw `Guid`), `Money` (shared kernel).

How: `PaymentAuthorized` carries `OrderId` as a raw `Guid` and `Amount` as `Money`. The amount on the Payment event records what was authorized; it does not have to equal the Sales-side `Order.Total` for the same `OrderId`. Partial billings, surcharges, and credits can produce divergence.

Pattern: Customer-Supplier with Shared Kernel for `Money`. `OrderFulfillmentProcessManager` issues the boundary command: on `OrderPlaced` it dispatches `AuthorizePayment` carrying the order total. See ADR 0006.

### Fulfillment to Billing

No direct flow: Fulfillment and Billing remain unaware of each other, and the process managers own any coordination between them. `ReturnProcessManager` is the worked case. It observes `ShipmentReturned`, restocks each returned line by dispatching `AdjustInventory` to Fulfillment, and reverses the payment in Billing by dispatching `VoidPayment` (a void, not a refund, because these flows authorize a payment but never capture it, so there is nothing to refund). It finds the payment through the `OrderIdToPaymentId` read model rather than reading Billing's state.

### Everything to Customer Support

Customer Support consumes the projections the other three contexts' events feed. Phase 6 shipped the four read models it reads: `OrderListProjection`, `OrderDetailProjection`, `CustomerSummaryProjection`, `InventoryDashboardProjection`. The read-side store interfaces follow ADR 0008: each store lives with the bounded context whose events it consumes, not in `Domain.Abstractions` (`IOrderListStore`, `IOrderDetailStore`, `ICustomerSummaryStore` in `Domain.Sales.ReadModels`; `IInventoryDashboardStore` in `Domain.Fulfillment.ReadModels`). Customer Support is a read-only composite, confirmed to add no write-side dependency.

## What does NOT cross

Some types stay strictly inside their context:

- **`Sales.OrderLine`** does not leave Sales. Carries `UnitPrice`, a Sales-only concern. Fulfillment receives a `LineId` reference but constructs its own `ReservationLine` and `ShipmentLine` types, each with the fields Fulfillment cares about (no `UnitPrice`).
- **`Sales.OrderStatus`** does not leave Sales. Fulfillment and Billing infer the order's state from the events they observe, not from a shared status enum.
- **`Fulfillment.ShipmentStatus`** and `Inventory`'s internal reservation state do not leave Fulfillment. They are aggregate-private.
- **`Billing.PaymentStatus`** and Payment's `_authorizedAmount` / `_capturedAmount` accounting do not leave Billing.

The rule, generalized from ADR 0007: each context translates incoming references into its own vocabulary. The translation cost concentrates at the boundary (the process managers) rather than smearing across every type.

## Process managers as the orchestration layer

The four contexts do not call each other. What coordinates them is a thin orchestration layer that sits beside them rather than inside any one of them: the process managers. They are not a fifth bounded context. They own no domain aggregates and publish no domain events the contexts consume. They are event-sourced workflows (each on its own stream, ADR 0011 and 0012) that observe context events and issue context commands.

Two ship in Phase 5:

- **`OrderFulfillmentProcessManager`** (`src/ProcessManagers/OrderFulfillment/`). Drives an order from placement to delivery: authorize payment, reserve inventory per line, schedule the shipment, complete on delivery. Its failure model is compensation: each step that can fail has an explicit branch that releases what earlier steps reserved and cancels the order.
- **`ReturnProcessManager`** (`src/ProcessManagers/Returns/`). Drives a return: restock the returned lines, void the payment. Its failure model is the deliberate contrast, a single `Stuck` terminal that halts for human intervention rather than compensating.

A process manager acquires data and effects change in exactly three ways:

1. **Load an aggregate, read-only, through `IEventStoreRepository<TAggregate>`.** When the orchestration needs data the triggering event does not carry, the PM loads the aggregate and reads it. `OrderFulfillmentProcessManager` loads the `Order` for its lines; `ReturnProcessManager` loads the `Shipment` for its `OrderId` and returned lines. The load is read-only and bounded by the aggregate's event count.
2. **Consult a read model for cross-context identifier translation.** A PM never reads another context's aggregate state for identifier translation, and never reads another process manager's state or stream. It consults a projection-maintained lookup: `SkuToInventoryId` to translate a Sales `Sku` into a Fulfillment `InventoryId`, `OrderIdToPaymentId` to translate a Sales `OrderId` into a Billing `PaymentId`. The lookup is a read model like any other (ADR 0008 places each store with the context whose events feed it).
3. **Dispatch a command through `ICausedCommandBus`.** A PM effects change only by sending commands, never by mutating an aggregate. The bus stamps causation so each command traces back to the event that triggered it, and idempotency keys make redelivery safe.

The read side has its own parallel rule for the same problem. A projection that needs an identifier the event payload does not carry maintains its own projection-private lookup table, populated by the event carrying both identifiers and queried by the event carrying one, and never resolves identity by loading an aggregate (ADR 0020). The write-side PM consults a shared read model (access pattern 2 above); the read-side projection maintains its own lookup, keyed to its own checkpoint so a rebuild cannot race a separate lookup projection. Both rules keep each layer off the aggregates it does not own. The worked cases: `order_list_shipments`, `order_detail_shipments`, and `order_detail_payments` resolve `OrderId` from shipment and payment events that carry only their own aggregate's id.

This is why the boundary crossings above all name a process manager: the translation cost the contexts refuse to carry concentrates here, in code that exists to coordinate.

## For book readers

This document is the worked example for Chapter 7's context-mapping section. The patterns Chapter 7 names in the abstract appear in the code:

- **Shared Kernel.** `Money`, `Currency`, `Address` in `Domain/SharedKernel/`. Jointly owned by Sales, Fulfillment, and Billing. See ADRs 0006 and 0009.
- **Customer-Supplier.** Sales publishes events; Fulfillment and Billing consume them. Translation lives at the consumer side, in the process managers. See ADRs 0005 and 0007.
- **Anti-Corruption Layer.** Phase 13 (Chapter 18's migration tooling) is where the ACL pattern appears for real, translating between a legacy CRUD system and the event-sourced domain. Phase 4's bounded-context boundaries do not need an ACL because all four contexts share the same teaching codebase and agree on the published event shapes directly. The book's emphasis on ACL for external integrations stays accurate; the reference implementation just doesn't have the legacy-system stress test until Phase 13.
- **Process-manager orchestration.** The two process managers in `src/ProcessManagers/` are Chapter 10's worked example: event-sourced workflows that coordinate the bounded contexts by observing their events and issuing their commands, with explicit compensation in `OrderFulfillmentProcessManager` and a single stuck terminal in `ReturnProcessManager`. The "Process managers as the orchestration layer" section above is the bridge from Chapter 10 to that code.

If you are reading the book and looking for the code, this folder is the bridge.
