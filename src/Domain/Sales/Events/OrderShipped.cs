using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderShipped(
    Guid OrderId,
    string Carrier,
    string TrackingNumber,
    DateTime ShippedUtc) : IDomainEvent;
