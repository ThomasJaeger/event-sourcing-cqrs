namespace EventSourcingCqrs.Domain.Abstractions;

// EventVersion is the schema version the event was stored under, positioned after EventType as it
// is on EventEnvelope. The dispatch path carries it from the outbox row so a consumer can resolve
// and upcast the payload to the current shape, the same version the store's own reads carry.
public sealed record OutboxMessage(
    long OutboxId,
    Guid EventId,
    string EventType,
    int EventVersion,
    IDomainEvent Event,
    EventMetadata Metadata,
    long GlobalPosition,
    int AttemptCount);
