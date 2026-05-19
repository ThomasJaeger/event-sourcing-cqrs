using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record ShipmentDelivered(
    Guid ShipmentId,
    DateTime DeliveredUtc) : IDomainEvent;
