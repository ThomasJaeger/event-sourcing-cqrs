# 0019. CustomerSummary Nets Cancellations Through Projection-Private State

## Status

Accepted (May 2026)

## Context

Phase 6 ships `CustomerSummaryProjection`, a per-customer aggregate read model deriving order count, lifetime value, and last-order date from Sales events. The projection subscribes to events keyed by `CustomerId`, accumulates totals across orders, and serves the per-customer query handler.

The shape of `OrderPlaced` and `OrderCancelled` forces a design call. `OrderPlaced(OrderId, CustomerId, Total, PlacedUtc)` carries the customer identity and the total. `OrderCancelled(OrderId, Reason, IssuedByUserId, CancelledUtc)` carries the order identity but no customer reference and no total. This is the lean-compensating-event shape Phase 5 established: compensating events carry correlation identity but not magnitude, because magnitude lives once on the original event.

Two coherent shapes for the projection:

- **No netting.** The projection subscribes to `OrderPlaced` only. Order count counts placements; lifetime value sums placement totals; last-order date is the max placement time. Cancellations do not appear in the read model. Simplest implementation; the read model represents gross placement activity.
- **Netting.** The projection subscribes to `OrderPlaced` and `OrderCancelled`. On cancellation, the projection recovers the cancelled order's customer and total, decrements the count, subtracts the total, and updates the read model. The read model represents retained business activity (placements that weren't subsequently cancelled).

The netted shape requires the projection to maintain its own state to recover the cancelled order's customer and total, because `OrderCancelled` doesn't carry them. The lookup pattern is the same one `InventoryDashboardProjection` uses for `InventoryReleased` (which carries no quantity): the projection holds a private lookup row keyed on the operation's correlation identity, looks it up on the reversal event, and reverses the accumulated state.

Chapter 13 does not depict `CustomerSummaryProjection` as a standalone projection. The chapter references `CustomerSummary` once in `ch13_listDeep_b` as a future projection that will populate the `customer_name` denormalization on `OpsOrderListProjection`. The chapter does not prescribe whether CustomerSummary nets. Phase 6 leads the manuscript on this surface; Phase 17 reconciles.

The business semantics argue for netting. A customer's lifetime value as a sum of placements without cancellations overstates retained business: a customer who placed and immediately cancelled appears as engaged as a customer who placed and kept. Read models that feed business decisions (retention analysis, upsell targeting, churn classification) want the netted number. A read model that wants the gross number can derive it from the event stream separately; the netted shape carries more decision value.

The lean-compensating-event pattern needs at least one worked case across Phase 6 to make the pattern visible at the aggregate granularity. `InventoryDashboardProjection`'s reverse-on-release works the pattern at per-reservation granularity, which is reasonably tight. `CustomerSummaryProjection`'s netting works it at per-customer-aggregate granularity. Both projections exercising the pattern give the codebase two concrete examples for future reference; only one would leave the pattern under-demonstrated.

## Decision

`CustomerSummaryProjection` nets cancellations out of its per-customer aggregates using a projection-private per-order lookup table. The projection subscribes to `OrderPlaced` and `OrderCancelled`.

On `OrderPlaced(OrderId, CustomerId, Total, PlacedUtc)`:

1. Upsert the `customer_summary` row keyed on `CustomerId`: increment `order_count` by 1, add `Total` to `lifetime_value_amount` (with currency-mismatch handling via the existing `Money` arithmetic), set `last_order_utc = MAX(last_order_utc, PlacedUtc)`, update `last_updated_utc`.
2. Insert a row into `customer_summary_orders` keyed on `(CustomerId, OrderId) PK`, carrying `total_amount, total_currency, placed_utc`. This is the projection-private lookup row.
3. Advance the checkpoint. All three writes happen under one unit-of-work transaction.

On `OrderCancelled(OrderId, Reason, IssuedByUserId, CancelledUtc)`:

1. Look up the `customer_summary_orders` row by `OrderId` (a secondary index supports this since the primary key is `(CustomerId, OrderId)` and the cancel event doesn't carry `CustomerId`).
2. If the row is not found, no-op the cancel with a debug-level log entry. This covers cancels of orders placed before the projection was deployed, cancels of orders where the `OrderPlaced` event has not yet been observed (rebuild ordering edge case), and cancels of non-existent orders.
3. If found, recover the row's `customer_id, total_amount, total_currency`. Decrement the `customer_summary.order_count` by 1, subtract the recovered total from `lifetime_value_amount`. Leave `last_order_utc` alone. Update `last_updated_utc`.
4. Delete the `customer_summary_orders` row.
5. Advance the checkpoint. All writes under one unit-of-work transaction.

The `last_order_utc` field intentionally does not recompute on cancellation. Recomputing would require scanning all `customer_summary_orders` rows for the customer to find the new max `placed_utc`, which is a non-trivial query on a hot path. The staleness is acceptable for v1: a customer's most-recent placement date might be slightly out of date relative to what's actually retained after cancellations, but the field's purpose (display in a customer summary view) tolerates the lag. A future query shape that needs the recomputed value can scan the lookup table on read, or a future projection can maintain a recomputed-on-cancel column alongside the lean one.

The `customer_summary_orders` table is projection-private state, not a separate read model exposed to query handlers. It lives in the `read_models` schema (per Session 0006's `read_models`-schema convention), but no `ICustomerSummaryOrdersStore` ships and no query handler reads it directly. The store interface and adapter contain it as an implementation detail of the projection's netting mechanism.

## Consequences

- `CustomerSummaryProjection` registers two `IEventHandler<TEvent>` interfaces (`OrderPlaced`, `OrderCancelled`). The `AddProjection<T>` helper forwards both to the singleton instance.
- Migration 0011 creates two tables: `customer_summary` (the public read model) and `customer_summary_orders` (the projection-private lookup), both in the `read_models` schema. A secondary index on `customer_summary_orders.order_id` supports the cancel-side lookup.
- `ICustomerSummaryUnitOfWork` exposes both tables' operations: `UpsertSummaryAsync`, `InsertOrderAsync`, `GetOrderByOrderIdAsync`, `DeleteOrderAsync`, plus the standard `CommitAsync(projectionName, position, ct)`. The store's `BeginAsync` returns the unit of work scoped to both tables under one transaction.
- Rebuild correctness: replaying `OrderPlaced` and `OrderCancelled` events from `GlobalPosition` zero produces the same `customer_summary` rows as live dispatch, regardless of ordering interleaving across customers. The rebuild test exercises this.
- The not-found-on-cancel path is structurally idempotent: rebuilding from an event log where a cancel arrives before its corresponding place (a non-realistic ordering, but a worst-case test) leaves the customer summary unchanged and the lookup table empty, which is correct given the truncated event history.
- Manuscript divergence against Chapter 13. `ch13_listDeep_b` mentions `CustomerSummary` as a future projection that populates `customer_name` for `OpsOrderListProjection`'s denormalization. Phase 6 ships `CustomerSummaryProjection` with a different purpose (per-customer aggregate stats) and a netted shape. Phase 17 reconciliation extends the chapter's depiction: either the chapter adds a worked CustomerSummary example with the netted shape, or the chapter's existing footnote re-frames against what Phase 6 ships. The flag rolls into the Phase 6 F-0010 block at session-log time.
- The lean-compensating-event pattern has two worked cases across Phase 6: this projection's per-customer netting on `OrderCancelled`, and `InventoryDashboardProjection`'s per-reservation reverse on `InventoryReleased`. The pattern's general shape (compensating event carries identity; magnitude is held by the projection's prior state; the lookup recovers magnitude for the reversal) is visible in both. Future projections that reverse aggregated state can follow either as a precedent.
- The `last_order_utc`-stays-stale call is an explicit trade-off. If a future business need wants the recomputed value, the change is additive: a second column carries the recomputed date, derived on `OrderCancelled` from a scan of the customer's remaining lookup rows. Adding the column does not invalidate the existing `last_order_utc` semantic; the two coexist.

## Trigger for revisiting

The netting commitment is reversible. Conditions that would justify reopening it:

- A query shape emerges that needs the gross placement count or gross lifetime value separately from the netted values. The fix is additive: the projection maintains `order_count_gross` and `lifetime_value_gross_amount` columns alongside the netted ones, incrementing-only on `OrderPlaced` without decrementing on `OrderCancelled`. The decision-revisit happens if the gross numbers become primary and the netted ones become secondary, at which point the projection-private lookup table's purpose changes.
- A business need wants `last_order_utc` recomputed on cancellation. The fix is additive (a second column with the recomputed value) or migratory (replacing the existing column's semantic), per the call at that time.
- The projection-private state pattern (`customer_summary_orders`) becomes a recurring shape that a third or fourth projection uses. At that point an ADR documenting the pattern generically would warrant the commitment, with this ADR and ADR 0017's worked-case-of-the-pattern role serving as antecedents.
- The lean-compensating-event shape itself is reconsidered (events grow magnitude on the cancellation side, making the lookup unnecessary). This is a different decision at a different layer; if it happens, this ADR's lookup pattern dissolves because the magnitude arrives on the event.

The trigger conditions are query-driven or business-driven, not pedagogical. The pedagogical question (what shape Chapter 13 should depict for CustomerSummary) belongs to Phase 17 reconciliation, not to a trigger reversal here.
