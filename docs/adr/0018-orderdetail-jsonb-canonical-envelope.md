# 0018. OrderDetail JSONB Timeline Carries a Canonical Envelope Per Entry

## Status

Accepted (May 2026)

## Context

Phase 6 ships `OrderDetailProjection`, which subscribes to sixteen events across Sales, Fulfillment, and Billing and derives a relational-plus-JSONB read model. The relational side carries the order header, line items, shipping address, and the four lifecycle timestamps. The JSONB side carries an event timeline: one entry per observed event, ordered by `GlobalPosition`, queryable by event type and time.

Three shapes for the JSONB entry were live during Session 0010 planning. (i) Full event payload as JSONB per row, no envelope. (ii) A canonical envelope per entry carrying `{type, occurredUtc, globalPosition, payload}`, with the payload as the full event JSON. (iii) A per-event-type discriminated union with explicit fields per type, structured as a tagged union in JSONB.

Shape (i) is cheapest to write on the projection side, but every consumer of the timeline has to know every event type's payload structure: there are no envelope fields to filter or sort on without deserializing the payload. Shape (iii) is the most type-safe and supports per-event-type indexed fields in JSONB, but couples the read-model schema to every domain event's shape; a payload field change propagates to a migration on the JSONB structure. Shape (ii) is the middle position: the envelope is stable and queryable, the payload is opaque and type-versioned.

Chapter 13's `ch13_detailDeep_code` depicts `OrderDetailProjection` as a four-event relational projection (Sales-only) with no JSONB timeline. The chapter does not prescribe a timeline shape because it does not depict the timeline. Phase 6 leads the manuscript on this surface; Phase 14 reconciles.

The C# event-handling stack separates envelope from payload at the type level. `EventContext<TEvent>(TEvent Event, EventMetadata Metadata, long GlobalPosition)` is the one shape a projection handler receives. `EventEnvelope` carries the same separation at the persistence layer. The JSONB timeline mirroring that separation produces cross-layer consistency: a query handler reading a timeline entry composes the same shape a live handler receives.

## Decision

`OrderDetailProjection`'s JSONB event timeline carries a canonical envelope per entry. The envelope structure:

```json
{
  "type": "OrderPlaced",
  "occurredUtc": "2026-05-01T14:23:11Z",
  "globalPosition": 12345,
  "payload": { "orderId": "...", "customerId": "...", "total": { "amount": 99.95, "currency": "USD" }, "placedUtc": "2026-05-01T14:23:11Z" }
}
```

`type` is the string token the event-type registry resolves; the convention follows whatever `IEventTypeProvider` registers (the simple CLR type name in current implementations, e.g. `"OrderPlaced"`). `occurredUtc` is `context.Metadata.OccurredUtc`, the event-record time. `globalPosition` is `context.GlobalPosition`, the read-model checkpoint position. `payload` is the full event payload serialized as JSONB, the same shape the event store persists for the events table.

Timeline entries persist to `read_models.order_detail_timeline` keyed on `(order_id, global_position) PK`, with `event_type TEXT NOT NULL` and `occurred_utc TIMESTAMPTZ NOT NULL` denormalized from the envelope fields for relational filtering and sorting without JSONB path predicates. The `payload JSONB NOT NULL` column carries the rest.

Query handlers reading the timeline filter on `event_type` and `occurred_utc` as relational columns. Payload deserialization happens only when an entry's full detail is needed for display, dispatched through the same `IEventTypeProvider` resolver the event-store deserialization path uses.

## Consequences

- `OrderDetailProjection` writes one row to `order_detail_timeline` per observed event, in addition to whatever relational columns the event updates. The unit-of-work transaction wraps both writes plus the checkpoint advance.
- The query-side `OrderDetailView` returns the timeline as `IReadOnlyList<OrderDetailTimelineRow>`, the envelope-rich rows verbatim: each carries `OrderId`, `GlobalPosition`, `EventType`, `OccurredUtc`, and the `Payload` as a JSON string. A consumer filters by `EventType` and `OccurredUtc` and deserializes the payload only for entry-specific detail; the handler does no deserialization on the read path. A future query shape can deserialize the payload to a typed object using the type-token resolution path; v1 returns the rows as stored.
- Rebuild correctness: replaying the sixteen events from `GlobalPosition` zero produces the same timeline rows in the same order as live dispatch. `OrderDetailRebuildTests` exercises this property directly.
- Type-token stability: the JSONB `type` field carries the same token `IEventTypeProvider` registers. If an event type rename ships in a future phase, the migration touches the timeline rows alongside the events table. Phase 11 (versioning) is the natural place to land this concern.
- Address handling is separate: the `Address` value object flattens to four relational columns on `order_detail` (`shipping_address_street`, `_city`, `_postal_code`, `_country`), not into the timeline payload's JSONB. The flattening preserves the cross-layer relational filtering option for queries that want it (orders shipping to a city, postal code, or country); the timeline still carries the full address inside the `ShippingAddressSet` payload.
- `Money` round-trips through JSONB as `{amount, currency}` per Phase 4's Fowler-pattern shape. The relational columns on `order_detail` carry the same two-field shape (`total_amount`, `total_currency`) per F-0005-03's convention. Cross-layer consistency: same shape on both sides of the relational-plus-JSONB split.
- Track A flag against Chapter 13. `ch13_detailDeep_code` depicts a four-event relational `OrderDetailProjection` with no JSONB timeline. Phase 14 reconciliation extends the depicted shape: the chapter either adds a JSONB-timeline subsection covering the canonical-envelope pattern, or names the timeline as a reference-implementation extension the chapter's worked example does not cover. The flag rolls into the Phase 6 F-0010 block at session-log time.
- The JSONB-vs-relational split rule for OrderDetail: header-level fields go relational (status, the four lifecycle timestamps, the totals, the flattened address); line items go relational (`order_detail_lines`); the event-by-event narrative goes JSONB (`order_detail_timeline`). Future detail views in other contexts can apply the same split decision per-projection; this ADR does not commit other projections to JSONB timelines.

## Trigger for revisiting

The canonical-envelope commitment is reversible. Conditions that would justify reopening it:

- A query pattern emerges that needs per-event-type indexed fields inside the timeline payload (sorting orders by their `OrderPlaced.Total` without joining to `order_detail`, filtering on `ShippingAddressSet.ShippingAddress.City` directly from the timeline). The current shape would require JSONB path expressions; shape (iii)'s per-event-type fields would index directly. If multiple such queries land, the shape widens to (iii) for the events those queries reach.
- The JSONB payload bloat produces measurable rebuild-time pain. A rebuild iterating over sixteen-events-times-orders' worth of full JSONB payloads, each containing the full event, may take longer than acceptable at scale. The first optimization is per-event-type payload pruning (the timeline retains only fields the UI needs, not the full event); the second is shape (i) with a smaller payload. Both moves leave the envelope-vs-payload structure intact.
- The type-token resolution path on the query side becomes a coupling concern (changing the registered token for an event requires migrating the timeline rows). Phase 11's versioning work would re-evaluate.

The trigger conditions are query-driven or operational, not pedagogical. The pedagogical question (what shape Chapter 13 should depict) belongs to Phase 14 reconciliation, not to a trigger reversal here.
