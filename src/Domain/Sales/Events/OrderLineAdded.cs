using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Sales.Events;

public sealed record OrderLineAdded(
    Guid OrderId,
    Guid LineId,
    string Sku,
    int Quantity,
    Money UnitPrice,
    DateTime AddedUtc) : IDomainEvent;
