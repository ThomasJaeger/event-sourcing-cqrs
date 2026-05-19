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
    // Stamps metadata for the first event a command handler raises. The
    // command's bus-generated CommandId becomes the event's CausationId, so
    // every event's causation chain ultimately points back to a command.
    public static EventMetadata ForCommand(ICommandContext context, int schemaVersion = 1)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: context.CorrelationId,
            CausationId: context.CausationCommandId,
            ActorId: context.ActorId,
            Source: context.ServiceName,
            SchemaVersion: schemaVersion,
            OccurredUtc: context.UtcNow().UtcDateTime);

    // Stamps metadata for an event caused by the prior event in the same
    // SaveAsync batch (Ch 8 line 1066). CausationId points to the prior
    // event's EventId; CorrelationId, ActorId, and Source carry forward.
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
