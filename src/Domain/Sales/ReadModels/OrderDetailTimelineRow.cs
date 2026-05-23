namespace EventSourcingCqrs.Domain.Sales.ReadModels;

// One entry of the OrderDetail JSONB event timeline: the C# shape of
// read_models.order_detail_timeline, keyed on (OrderId, GlobalPosition). One row per
// observed event (D1). EventType is the event's CLR type name (typeof(TEvent).Name),
// which equals the event store's registered type token by construction: the Postgres
// EventTypeRegistry defaults the storage name to Type.Name and every provider
// registers bare typeof(X), so the projection writes the CLR name with no layering
// dependency on that registry. OccurredUtc and GlobalPosition come from the envelope,
// not the payload. Payload is the full event serialized as JSON text, written into
// the JSONB column by the adapter. ADR 0018.
public sealed record OrderDetailTimelineRow(
    Guid OrderId,
    long GlobalPosition,
    string EventType,
    DateTime OccurredUtc,
    string Payload);
