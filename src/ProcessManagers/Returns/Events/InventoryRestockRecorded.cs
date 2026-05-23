using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.ProcessManagers.Returns.Events;

// Every returned line was restocked: the AdjustInventory fan-out completed with
// all lines succeeding. Recorded once after the fan-out, not per line; the
// per-line AdjustInventory dispatches and their idempotency keys carry the
// line-level detail.
public sealed record InventoryRestockRecorded : IProcessManagerEvent;
