using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.EventStore.ContractTests;

// Envelope construction for the suite, and for the backends that have to write a row by hand
// to park a held writer. OccurredUtc is a frozen literal and no fact ever asserts on it: the
// contract orders by global position and never by time, so an engine's clock is not the
// suite's business.
public static class ContractEnvelopes
{
    private static readonly DateTime FrozenOccurredUtc =
        new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    public static StreamId NewStreamId()
        => StreamId.Parse($"contract:{Guid.NewGuid():N}");

    public static StreamId NewProcessManagerStreamId()
        => StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    // The tenant rides on the metadata, not on the stream id. StreamId only composes a tenant
    // for process-manager streams, and the tenant-scoped read filters on the tenant the
    // envelope's metadata carries, so metadata is the seam a tenant fact has to drive.
    // source is a knob because it is the one free-text field on the metadata, which makes it the
    // only place the suite can drive non-ASCII content through the metadata column.
    public static EventEnvelope Build(
        StreamId streamId,
        int streamVersion,
        IDomainEvent payload,
        Guid? eventId = null,
        TenantId? tenant = null,
        string? source = null)
    {
        var id = eventId ?? Guid.NewGuid();
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: BuildMetadata(id, tenant, source),
            OccurredUtc: FrozenOccurredUtc,
            GlobalPosition: 0);
    }

    public static ProcessManagerEventEnvelope BuildProcessManager(
        StreamId streamId,
        int streamVersion,
        IProcessManagerEvent payload,
        Guid? eventId = null,
        TenantId? tenant = null)
    {
        var id = eventId ?? Guid.NewGuid();
        return new ProcessManagerEventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: BuildMetadata(id, tenant, source: null),
            OccurredUtc: FrozenOccurredUtc,
            GlobalPosition: 0);
    }

    private static EventMetadata BuildMetadata(Guid eventId, TenantId? tenant, string? source)
        => new(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: source ?? "contract-suite",
            SchemaVersion: 1,
            OccurredUtc: FrozenOccurredUtc,
            Tenant: tenant ?? WellKnownTenants.Default);
}
