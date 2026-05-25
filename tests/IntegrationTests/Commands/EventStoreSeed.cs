using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.IntegrationTests.Commands;

internal static class EventStoreSeed
{
    private static readonly DateTime SeedUtc =
        new DateTime(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    public static async Task AppendAsync(
        IEventStore eventStore,
        StreamId streamId,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken ct = default)
    {
        var envelopes = events
            .Select((payload, i) => BuildEnvelope(streamId, streamVersion: i + 1, payload))
            .ToList();
        await eventStore.AppendAsync(streamId, expectedVersion: 0, envelopes, ct);
    }

    private static EventEnvelope BuildEnvelope(
        StreamId streamId, int streamVersion, IDomainEvent payload)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "integration-test-seed",
            SchemaVersion: 1,
            OccurredUtc: SeedUtc);
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
