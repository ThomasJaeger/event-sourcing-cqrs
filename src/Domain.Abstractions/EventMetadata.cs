using System.Text.Json.Serialization;

namespace EventSourcingCqrs.Domain.Abstractions;

public sealed record EventMetadata(
    Guid EventId,
    Guid CorrelationId,
    Guid CausationId,
    Guid ActorId,
    string Source,
    DateTime OccurredUtc,
    [property: JsonPropertyName("tenant_id")] TenantId Tenant)
{
    // Stamps metadata for the first event a command handler raises. The
    // command's bus-generated CommandId becomes the event's CausationId, so
    // every event's causation chain ultimately points back to a command. The
    // tenant is supplied by the caller, which on the command path reads it from
    // the current-tenant accessor; one resolved tenant feeds both the stream id
    // and the metadata so the two cannot disagree.
    public static EventMetadata ForCommand(ICommandContext context, TenantId tenant)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: context.CorrelationId,
            CausationId: context.CausationCommandId,
            ActorId: context.ActorId,
            Source: context.ServiceName,
            OccurredUtc: context.UtcNow().UtcDateTime,
            Tenant: tenant);

    // Stamps metadata for an event caused by the prior event in the same
    // SaveAsync batch (Ch 8 line 1066). CausationId points to the prior event's
    // EventId; CorrelationId, ActorId, Source, and the tenant carry forward (a
    // caused event belongs to the same tenant as the event that caused it).
    public EventMetadata ForCausedEvent(DateTime occurredUtc)
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: CorrelationId,
            CausationId: EventId,
            ActorId: ActorId,
            Source: Source,
            OccurredUtc: occurredUtc,
            Tenant: Tenant);
}
