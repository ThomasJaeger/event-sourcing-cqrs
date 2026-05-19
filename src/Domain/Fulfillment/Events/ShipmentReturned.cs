using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record ShipmentReturned(
    Guid ShipmentId,
    string Reason,
    DateTime ReturnedUtc) : IDomainEvent;
