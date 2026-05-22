using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// A previously-reserved line was released during compensation. Carries only the
// line identifier; InventoryId and Quantity are already in the PM's reservation
// state from the earlier ReservationSucceeded.
public sealed record ReservationReleased(Guid LineId) : IProcessManagerEvent;
