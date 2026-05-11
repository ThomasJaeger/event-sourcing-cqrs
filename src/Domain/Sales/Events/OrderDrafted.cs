using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderDrafted(
    Guid OrderId,
    Guid CustomerId,
    DateTime DraftedUtc) : IDomainEvent;
