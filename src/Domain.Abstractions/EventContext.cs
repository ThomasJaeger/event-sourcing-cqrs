namespace EventSourcingCqrs.Domain.Abstractions;

// What an event handler needs to do its job: the typed payload, the event
// metadata, and the global position. A projection checkpoints on
// GlobalPosition and reads business-time and system-time fields off Metadata.
// One context shape serves both live dispatch (through the outbox) and replay
// (through the ProjectionReplayer), so handlers need no second signature.
public sealed record EventContext<TEvent>(
    TEvent Event,
    EventMetadata Metadata,
    long GlobalPosition)
    where TEvent : IDomainEvent;
