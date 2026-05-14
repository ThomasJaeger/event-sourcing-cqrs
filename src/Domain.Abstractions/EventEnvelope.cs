namespace EventSourcingCqrs.Domain.Abstractions;

// The C# boundary type for a stored event: the payload plus the metadata the
// store needs to place it. GlobalPosition is assigned by the event store on
// append, so write-path construction passes 0 and a read populates the real
// value. PostgreSQL IDENTITY starts at 1, so 0 reads as "the store has not
// touched this yet" without ever colliding with a stored row.
public sealed record EventEnvelope(
    Guid StreamId,
    int StreamVersion,
    Guid EventId,
    string EventType,
    int EventVersion,
    IDomainEvent Payload,
    EventMetadata Metadata,
    DateTime OccurredUtc,
    long GlobalPosition);
