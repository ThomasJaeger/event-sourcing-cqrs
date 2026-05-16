using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;

namespace EventSourcingCqrs.Domain.Sales;

// Sales bounded context's contribution to the event type registry. Per
// bounded context, not per aggregate: Phase 4's Fulfillment provider will
// own both Inventory and Shipment events in the same shape.
public sealed class SalesEventTypeProvider : IEventTypeProvider
{
    // Canonical lifecycle order: drafting, line edits, address, placement,
    // fulfillment outcome. Storage names default to the CLR type name; the
    // provider stays in step by relying on Register(Type) rather than supplying
    // explicit names.
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(OrderDrafted),
        typeof(OrderLineAdded),
        typeof(OrderLineRemoved),
        typeof(ShippingAddressSet),
        typeof(OrderPlaced),
        typeof(OrderShipped),
        typeof(OrderCancelled),
    ];
}
