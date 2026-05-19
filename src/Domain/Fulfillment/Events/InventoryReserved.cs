using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record InventoryReserved(
    Guid InventoryId,
    Guid OrderId,
    Guid LineId,
    string Sku,
    int Quantity,
    DateTime ReservedUtc) : IDomainEvent;
