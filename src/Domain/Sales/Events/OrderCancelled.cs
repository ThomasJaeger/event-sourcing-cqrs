using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderCancelled(
    Guid OrderId,
    string Reason,
    Guid IssuedByUserId,
    DateTime CancelledUtc) : IDomainEvent;
