using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record ShippingAddressSet(
    Guid OrderId,
    Address ShippingAddress,
    DateTime SetUtc) : IDomainEvent;
