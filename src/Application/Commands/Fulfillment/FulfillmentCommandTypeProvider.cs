using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Commands.Fulfillment;

// Fulfillment bounded context's user-dispatched commands for HTTP by-name
// dispatch (ADR 0023). The inventory create and adjust, then the shipment
// dispatch, deliver, and return. ReserveInventory, ReleaseInventory, and
// ScheduleShipment are process-manager-dispatched, not user-initiated, so they
// do not register here.
public sealed class FulfillmentCommandTypeProvider : ICommandTypeProvider
{
    public IEnumerable<Type> GetCommandTypes() =>
    [
        typeof(CreateInventory),
        typeof(AdjustInventory),
        typeof(DispatchShipment),
        typeof(DeliverShipment),
        typeof(ReturnShipment),
    ];
}
