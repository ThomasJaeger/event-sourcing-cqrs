using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// The PM dispatched ScheduleShipment with a PM-chosen ShipmentId, recorded here.
// The ShipmentDispatched and ShipmentDelivered events carry only ShipmentId, so
// their handlers re-load the Shipment aggregate to recover OrderId (ADR 0015);
// this stored value lets the PM confirm an inbound shipment event is the one it
// scheduled.
public sealed record ShipmentSchedulingRequested(Guid ShipmentId) : IProcessManagerEvent;
