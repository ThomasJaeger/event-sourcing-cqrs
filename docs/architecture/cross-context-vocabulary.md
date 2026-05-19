# Cross-Context Vocabulary

This document names what crosses each bounded-context boundary in the reference implementation, and what does not. It is the companion to the four context-mapping ADRs (0005, 0006, 0007, 0008) and to Chapter 7's context-mapping section in the book. Readers coming to the code first should find here the answer to "why does Fulfillment carry an `OrderId` field but never reference Sales' `Order` type?"

## Purpose

The reference implementation has four bounded contexts that share information without sharing types. The shape is opinionated: identifiers travel as raw `Guid`, monetary amounts travel through a small shared kernel, and every context defines its own line types and aggregate state. The substantive decisions live in ADRs 0005 through 0008. This document gathers them into one place so a reader can see the cross-cutting picture in one read.

The dominant pattern is Customer-Supplier from Chapter 7. Sales publishes events. Fulfillment and Billing consume them. The downstream contexts do not import Sales' types; they translate at the boundary. The boundary translation lives in the Phase 5 process manager, not in a shared kernel.

## The four contexts

**Sales** (`src/Domain/Sales/`). Owns `Order` and `OrderLine`. Publishes `OrderDrafted`, `OrderLineAdded`, `OrderLineRemoved`, `ShippingAddressSet`, `OrderPlaced`, `OrderShipped`, `OrderCancelled`. Sales is the source of order identity (`OrderId`), customer identity (`CustomerId`), and per-order pricing (`Money UnitPrice`).

**Fulfillment** (`src/Domain/Fulfillment/`). Owns `Inventory` and `Shipment`. Publishes `InventoryCreated`, `InventoryAdjusted`, `InventoryReserved`, `InventoryReleased`, `ShipmentScheduled`, `ShipmentDispatched`, `ShipmentDelivered`, `ShipmentReturned`. Fulfillment cares about SKUs and quantities. It does not care about pricing.

**Billing** (`src/Domain/Billing/`). Owns `Payment`. Publishes `PaymentAuthorized`, `PaymentCaptured`, `PaymentRefunded`, `PaymentVoided`. Billing cares about amounts and payment-method references. It does not care about line items or shipping.

**Customer Support** (Phase 6+). No own aggregates. Reads from the projections published by the other three contexts. Its model is a read-only composite view; the Phase 6 work fleshes this out.

## The shared kernel

`src/Domain/SharedKernel/` holds three types shared by multiple contexts:

- **`Money`** (Sales and Billing). Fowler's pattern from PoEAA: typed `Currency`, arithmetic operators that throw on currency mismatch, allocation methods. See ADR 0006 (the cross-context sharing decision) and ADR 0009 (the Fowler-pattern shape).
- **`Currency`** (used through `Money`). Typed value object with ISO-4217 format validation and access-time precision lookup. See ADR 0009.
- **`Address`** (Sales and Fulfillment). Simple value record. Sales stores it on `Order` after `ShippingAddressSet`; Fulfillment carries it on `ShipmentScheduled` as the delivery destination.

What is **not** in the shared kernel: identifier types. `OrderId`, `CustomerId`, `LineId`, `PaymentId`, `ShipmentId`, `InventoryId` all travel as raw `Guid`. Chapter 7 lists "common identifier types" as a typical Shared Kernel candidate; this codebase deliberately does not promote IDs to typed wrappers. See ADR 0005 for the reasoning.

The shared kernel is deliberately small. Adding to it requires the sharing contexts to agree. The pattern is the one Chapter 7 names: small and stable, jointly owned.

## The boundary crossings

This section names what crosses each pair of contexts. The pattern is Customer-Supplier in every case where Phase 4 ships a real boundary.

### Sales to Fulfillment

What crosses: `OrderId` (raw `Guid`), `LineId` (raw `Guid`), `Sku` (string), `Quantity` (int), `Address` (shared kernel).

How: Fulfillment events carry `OrderId` and `LineId` as raw `Guid` fields. Examples: `InventoryReserved(InventoryId, OrderId, LineId, Sku, Quantity, ReservedUtc)`, `ShipmentScheduled(ShipmentId, OrderId, Destination, Lines, ScheduledUtc)`. Fulfillment does not reference `Sales.Order` or `Sales.OrderLine` types directly. Verified on disk: `grep -rn "using EventSourcingCqrs.Domain.Sales" src/Domain/Fulfillment/` returns zero hits.

Pattern: Customer-Supplier (Chapter 7). The Phase 5 process manager translates from `OrderLineAdded` events into `ReserveInventory` and `ScheduleShipment` commands. The translation is the boundary work; this doc names it so Phase 5 inherits a known seam rather than a discovered one.

See ADR 0005 (raw Guid for cross-context IDs) and ADR 0007 (per-context line types).

### Sales to Billing

What crosses: `OrderId` (raw `Guid`), `Money` (shared kernel).

How: `PaymentAuthorized` carries `OrderId` as a raw `Guid` and `Amount` as `Money`. The amount on the Payment event records what was authorized; it does not have to equal the Sales-side `Order.Total` for the same `OrderId`. Partial billings, surcharges, and credits can produce divergence.

Pattern: Customer-Supplier with Shared Kernel for `Money`. See ADR 0006.

### Fulfillment to Billing

No direct flow in Phase 4. The Phase 5 process manager coordinates by issuing commands to both contexts in response to upstream events; Fulfillment and Billing remain unaware of each other. If a future need surfaces (a Refund triggered by a Return, for example), the process manager owns the coordination, not a direct Fulfillment-to-Billing event.

### Everything to Customer Support

Phase 6+. Customer Support consumes projections published by the other three contexts. The read-side store interfaces follow ADR 0008: each store lives with the bounded context whose events it consumes, not in `Domain.Abstractions`. Customer Support is a read-only composite; the Phase 6 work confirms it adds no write-side dependencies.

## What does NOT cross

Some types stay strictly inside their context:

- **`Sales.OrderLine`** does not leave Sales. Carries `UnitPrice`, a Sales-only concern. Fulfillment receives a `LineId` reference but constructs its own `ReservationLine` and `ShipmentLine` types, each with the fields Fulfillment cares about (no `UnitPrice`).
- **`Sales.OrderStatus`** does not leave Sales. Fulfillment and Billing infer the order's state from the events they observe, not from a shared status enum.
- **`Fulfillment.ShipmentStatus`** and `Inventory`'s internal reservation state do not leave Fulfillment. They are aggregate-private.
- **`Billing.PaymentStatus`** and Payment's `_authorizedAmount` / `_capturedAmount` accounting do not leave Billing.

The rule, generalized from ADR 0007: each context translates incoming references into its own vocabulary. The translation cost concentrates at the boundary (the process manager) rather than smearing across every type.

## For book readers

This document is the worked example for Chapter 7's context-mapping section. The patterns Chapter 7 names in the abstract appear in the code:

- **Shared Kernel.** `Money`, `Currency`, `Address` in `Domain/SharedKernel/`. Jointly owned by Sales, Fulfillment, and Billing. See ADRs 0006 and 0009.
- **Customer-Supplier.** Sales publishes events; Fulfillment and Billing consume them. Translation lives at the consumer side, in the Phase 5 process manager. See ADRs 0005 and 0007.
- **Anti-Corruption Layer.** Phase 13 (Chapter 18's migration tooling) is where the ACL pattern appears for real, translating between a legacy CRUD system and the event-sourced domain. Phase 4's bounded-context boundaries do not need an ACL because all four contexts share the same teaching codebase and agree on the published event shapes directly. The book's emphasis on ACL for external integrations stays accurate; the reference implementation just doesn't have the legacy-system stress test until Phase 13.

If you are reading the book and looking for the code, this folder is the bridge.
