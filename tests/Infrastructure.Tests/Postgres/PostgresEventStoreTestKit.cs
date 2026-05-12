using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Shared setup for the PostgresEventStore test classes. Kept narrow:
// just the JSON options, the registry factory, and the envelope builder.
// Each test class owns its container, data source, and store instances.
internal static class PostgresEventStoreTestKit
{
    public static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    public static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry()
            .Register<TestPayload>()
            .Register<OtherTestPayload>();

    public static EventEnvelope BuildEnvelope(
        Guid streamId,
        int streamVersion,
        IDomainEvent payload,
        DateTime? occurredUtc = null,
        Guid? correlationId = null,
        Guid? eventId = null)
    {
        var when = occurredUtc ?? new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var id = eventId ?? Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: id,
            CorrelationId: correlationId ?? Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: when);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: when);
    }
}

internal sealed record TestPayload(Guid OrderId, decimal Total) : IDomainEvent;
internal sealed record OtherTestPayload(string Description) : IDomainEvent;
