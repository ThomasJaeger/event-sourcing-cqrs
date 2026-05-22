using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.OrderFulfillment.Events;

// One line reserved successfully. InventoryId is the resolved target the
// compensating ReleaseInventory will name; Sku and Quantity are per-line forensic
// detail.
public sealed record ReservationSucceeded(
    Guid LineId,
    string Sku,
    int Quantity,
    Guid InventoryId) : IProcessManagerEvent;
