using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// One line failed to reserve, learned from the ReserveInventory CommandOutcome.
// Reason carries the failure detail for forensics and the compensation decision.
public sealed record ReservationFailed(
    Guid LineId,
    string Sku,
    int Quantity,
    string Reason) : IProcessManagerEvent;
