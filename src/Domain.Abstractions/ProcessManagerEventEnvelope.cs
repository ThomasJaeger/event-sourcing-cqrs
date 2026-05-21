namespace EventSourcingCqrs.Domain.Abstractions;

// The C# boundary type for a stored PM event, parallel to EventEnvelope but
// carrying an IProcessManagerEvent payload (ADR 0013). EventMetadata is reused
// unchanged; it carries no payload reference. GlobalPosition is store-assigned
// on append, so write-path construction passes 0.
public sealed record ProcessManagerEventEnvelope(
    StreamId StreamId,
    int StreamVersion,
    Guid EventId,
    string EventType,
    int EventVersion,
    IProcessManagerEvent Payload,
    EventMetadata Metadata,
    DateTime OccurredUtc,
    long GlobalPosition);
