namespace EventSourcingCqrs.Domain.Abstractions;

public sealed record EventMetadata(
    Guid EventId,
    Guid CorrelationId,
    Guid CausationId,
    Guid ActorId,
    string Source,
    int SchemaVersion,
    DateTime OccurredUtc)
{
    public EventMetadata ForCausedEvent(DateTime occurredUtc, int schemaVersion = 1)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: CorrelationId,
            CausationId: EventId,
            ActorId: ActorId,
            Source: Source,
            SchemaVersion: schemaVersion,
            OccurredUtc: occurredUtc);
}
