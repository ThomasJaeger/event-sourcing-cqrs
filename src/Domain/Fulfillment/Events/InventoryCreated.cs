using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record InventoryCreated(
    Guid InventoryId,
    string Sku,
    DateTime CreatedUtc) : IDomainEvent;
