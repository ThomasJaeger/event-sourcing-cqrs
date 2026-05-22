using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// Records the observed ShipmentDispatched. The PM now waits for delivery.
public sealed record ShipmentDispatchRecorded : IProcessManagerEvent;
