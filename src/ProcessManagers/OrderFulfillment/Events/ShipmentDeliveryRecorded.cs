using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// Records the observed ShipmentDelivered. State holds at AwaitingDelivery until
// the MarkOrderCompleted dispatch succeeds and Complete records the terminal.
public sealed record ShipmentDeliveryRecorded : IProcessManagerEvent;
