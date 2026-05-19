using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;

namespace EventSourcingCqrs.Domain.Fulfillment;

// Fulfillment bounded context's contribution to the event type registry. Per
// bounded context, not per aggregate: Inventory's four events list here; the
// Shipment aggregate's events will be added when the Shipment scaffolding
// lands later in this session.
public sealed class FulfillmentEventTypeProvider : IEventTypeProvider
{
    // Canonical lifecycle order: creation, stock adjustment, reservation
    // lifecycle.
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(InventoryCreated),
        typeof(InventoryAdjusted),
        typeof(InventoryReserved),
        typeof(InventoryReleased),
    ];
}
