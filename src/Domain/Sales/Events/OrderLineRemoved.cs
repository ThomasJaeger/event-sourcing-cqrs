using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderLineRemoved(
    Guid OrderId,
    Guid LineId,
    DateTime RemovedUtc) : IDomainEvent;
