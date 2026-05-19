using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record InventoryReleased(
    Guid InventoryId,
    Guid OrderId,
    Guid LineId,
    string Reason,
    DateTime ReleasedUtc) : IDomainEvent;
