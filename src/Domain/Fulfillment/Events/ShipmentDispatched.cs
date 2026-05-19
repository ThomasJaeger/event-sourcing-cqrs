using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record ShipmentDispatched(
    Guid ShipmentId,
    string CarrierReference,
    DateTime DispatchedUtc) : IDomainEvent;
