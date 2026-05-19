using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.SharedKernel;

namespace EventSourcingCqrs.Domain.Fulfillment.Events;

public sealed record ShipmentScheduled(
    Guid ShipmentId,
    Guid OrderId,
    Address Destination,
    IReadOnlyList<ShipmentLine> Lines,
    DateTime ScheduledUtc) : IDomainEvent;
