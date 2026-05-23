using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

// The order's own lifecycle terminal, raised when the OrderFulfillment process
// manager marks it completed on shipment delivery (Decision 13). Distinct from
// the Shipment aggregate's delivery: this records the Order reaching its terminal,
// not the physical shipment's state.
public sealed record OrderCompleted(Guid OrderId, DateTime CompletedUtc) : IDomainEvent;
