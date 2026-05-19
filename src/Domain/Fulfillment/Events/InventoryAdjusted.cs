using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record InventoryAdjusted(
    Guid InventoryId,
    string Sku,
    int QuantityDelta,
    string Reason,
    DateTime AdjustedUtc) : IDomainEvent;
