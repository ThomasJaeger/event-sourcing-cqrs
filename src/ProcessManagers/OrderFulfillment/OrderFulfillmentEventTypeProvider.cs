using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment;

// Registers OrderFulfillmentProcessManager's twelve PM-internal event types into
// the ProcessManagerEventTypeRegistry (ADR 0013). AddPostgresEventStore walks
// every registered IProcessManagerEventTypeProvider; WorkersHostFactory registers
// this one. Listed in workflow order: start, payment, the per-line reservation
// pair, reservations-complete, the shipment trio, then the compensation events
// and the terminal.
public sealed class OrderFulfillmentEventTypeProvider : IProcessManagerEventTypeProvider
{
    public IEnumerable<Type> GetEventTypes() =>
    [
        typeof(OrderFulfillmentStarted),
        typeof(PaymentAuthorizationRecorded),
        typeof(ReservationSucceeded),
        typeof(ReservationFailed),
        typeof(InventoryReservationsCompleted),
        typeof(ShipmentSchedulingRequested),
        typeof(ShipmentDispatchRecorded),
        typeof(ShipmentDeliveryRecorded),
        typeof(OrderFulfillmentCancellationStarted),
        typeof(ReservationReleased),
        typeof(PaymentVoidRequested),
        typeof(OrderFulfillmentCompleted),
    ];
}
