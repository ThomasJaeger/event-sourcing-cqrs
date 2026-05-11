namespace EventSourcingCqrs.Domain.Abstractions;

public sealed record EventEnvelope(
    Guid StreamId,
    int StreamVersion,
    Guid EventId,
    string EventType,
    int EventVersion,
    IDomainEvent Payload,
    EventMetadata Metadata,
    DateTime OccurredUtc);
