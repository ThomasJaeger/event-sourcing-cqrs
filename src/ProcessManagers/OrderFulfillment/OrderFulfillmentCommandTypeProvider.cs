using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Registers the OrderFulfillment PM's timeout commands into CommandTypeRegistry so
// the delay queue can round-trip them by type name (ADR 0017), parallel to
// OrderFulfillmentEventTypeProvider for the PM's events. The bounded-context
// commands the PM dispatches outward are never scheduled, so they do not register
// here.
public sealed class OrderFulfillmentCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() =>
    [
        typeof(TimeoutAwaitingPaymentForOrder),
        typeof(TimeoutAwaitingDispatchForOrder),
    ];
}
