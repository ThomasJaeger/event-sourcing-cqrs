using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    Money Total,
    DateTime PlacedUtc) : IDomainEvent;
