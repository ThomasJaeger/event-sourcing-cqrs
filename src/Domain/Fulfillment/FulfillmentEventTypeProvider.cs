using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;

namespace EventSourcingCqrs.Domain.Fulfillment;

// Fulfillment bounded context's contribution to the event type registry.
// Per bounded context, not per aggregate: Inventory's four events come
// first, Shipment's four next, each block in canonical lifecycle order.
public sealed class FulfillmentEventTypeProvider : IEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(InventoryCreated),
        typeof(InventoryAdjusted),
        typeof(InventoryReserved),
        typeof(InventoryReleased),
        typeof(ShipmentScheduled),
        typeof(ShipmentDispatched),
        typeof(ShipmentDelivered),
        typeof(ShipmentReturned),
    ];
}
