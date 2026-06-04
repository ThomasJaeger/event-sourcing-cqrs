using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.IntegrationTests.Commands;

internal static class EventStoreSeed
{
    private static readonly DateTime SeedUtc =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public static Task AppendAsync(
        IEventStore eventStore,
        StreamId streamId,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken ct = default)
        => AppendAsync(eventStore, streamId, events, WellKnownTenants.Default, ct);

    // Seeds an aggregate stream under a chosen tenant, so a cross-tenant test can plant a same-id
    // aggregate under a second tenant. The caller passes a stream id built for that tenant and the
    // matching tenant here, so the metadata tenant and the events-table tenant_id agree with the
    // stream id's tenant segment. An overload rather than a defaulted parameter because TenantId is
    // not a constant (no compile-time default) and a CancellationToken parameter must stay last.
    public static async Task AppendAsync(
        IEventStore eventStore,
        StreamId streamId,
        IReadOnlyList<IDomainEvent> events,
        TenantId tenant,
        CancellationToken ct = default)
    {
        var envelopes = events
            .Select((payload, i) => BuildEnvelope(streamId, streamVersion: i + 1, payload, tenant))
            .ToList();
        await eventStore.AppendAsync(streamId, expectedVersion: 0, envelopes, ct);
    }

    private static EventEnvelope BuildEnvelope(
        StreamId streamId, int streamVersion, IDomainEvent payload, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "integration-test-seed",
            SchemaVersion: 1,
            OccurredUtc: SeedUtc,
            Tenant: tenant);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: SeedUtc,
            GlobalPosition: 0);
    }
}
