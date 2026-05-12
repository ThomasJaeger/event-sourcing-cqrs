namespace EventSourcingCqrs.Domain.Abstractions;

public sealed record OutboxMessage(
    long OutboxId,
    Guid EventId,
    string EventType,
    IDomainEvent Event,
    EventMetadata Metadata,
    int AttemptCount);
