using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Shared setup for the PostgresEventStore test classes. Kept narrow:
// just the JSON options, the registry factory, and the envelope builder.
// Each test class owns its container, data source, and store instances.
internal static class PostgresEventStoreTestKit
{
    // Delegates to the one event-store serialization seam (ADR 0048) rather than rebuilding its
    // shape. The helper stays because the call sites across this directory name it.
    public static JsonSerializerOptions CreateJsonOptions()
        => EventStoreJsonOptions.Create();

    public static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry()
            .Register<TestPayload>()
            .Register<OtherTestPayload>();

    public static ProcessManagerEventTypeRegistry CreatePmRegistry()
        => new ProcessManagerEventTypeRegistry()
            .Register<TestPmPayload>();

    public static StreamId NewStreamId()
        => StreamId.Parse($"test:{Guid.NewGuid():N}");

    // A pm- prefixed stream, the form AppendProcessManagerEventsAsync requires and
    // the aggregate feeds exclude (ADR 0013). The correlation trace reads both
    // families out of the one events table, so its suite needs to seed both.
    public static StreamId NewPmStreamId()
        => StreamId.ForProcessManager(
            StreamPrefixes.OrderFulfillmentPm, WellKnownTenants.Default, Guid.NewGuid());

    public static EventEnvelope BuildEnvelope(
        StreamId streamId,
        int streamVersion,
        IDomainEvent payload,
        DateTime? occurredUtc = null,
        Guid? correlationId = null,
        Guid? eventId = null,
        TenantId? tenant = null)
    {
        var when = occurredUtc ?? new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var id = eventId ?? Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: id,
            CorrelationId: correlationId ?? Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            OccurredUtc: when,
            Tenant: tenant ?? WellKnownTenants.Default);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: when,
            GlobalPosition: 0);
    }

    // The PM twin of BuildEnvelope, same knobs. PM rows land in the same events
    // table as aggregate rows and carry the same EventMetadata, so a suite that
    // reads the table by correlation seeds both through one shape.
    public static ProcessManagerEventEnvelope BuildPmEnvelope(
        StreamId streamId,
        int streamVersion,
        IProcessManagerEvent payload,
        DateTime? occurredUtc = null,
        Guid? correlationId = null,
        Guid? eventId = null,
        TenantId? tenant = null)
    {
        var when = occurredUtc ?? new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var id = eventId ?? Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: id,
            CorrelationId: correlationId ?? Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            OccurredUtc: when,
            Tenant: tenant ?? WellKnownTenants.Default);
        return new ProcessManagerEventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: when,
            GlobalPosition: 0);
    }
}

internal sealed record TestPayload(Guid OrderId, decimal Total) : IDomainEvent;
internal sealed record OtherTestPayload(string Description) : IDomainEvent;
internal sealed record TestPmPayload(int Step) : IProcessManagerEvent;
