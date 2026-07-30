using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Migration.Demo;

// Chapter 18: appends a run of domain events to one aggregate stream, the shape the CDC reader and
// the outbox emitter share. It numbers versions off the stream's current tail and stamps each event's
// metadata, so the two callers differ only in what they carry in (their source, actor, and per-event
// correlation and occurred-time) rather than in how they write.
public sealed class EventStreamAppender
{
    private readonly IEventStore _eventStore;
    private readonly ICurrentEventSchemaVersions _schemaVersions;

    public EventStreamAppender(IEventStore eventStore, ICurrentEventSchemaVersions schemaVersions)
    {
        _eventStore = eventStore;
        _schemaVersions = schemaVersions;
    }

    public async Task AppendAsync(
        StreamId streamId,
        IReadOnlyList<AppendedEvent> events,
        string source,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        // The append contract enforces optimistic concurrency on (stream_id, stream_version), so the
        // caller numbers versions off the stream's current tail and passes it as the expected version.
        var existing = await _eventStore.ReadStreamAsync(streamId, 0, cancellationToken);
        var baseVersion = existing.Count == 0 ? 0 : existing[^1].StreamVersion;

        var envelopes = new List<EventEnvelope>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            envelopes.Add(BuildEnvelope(streamId, baseVersion + i + 1, events[i], source, actorId));
        }

        await _eventStore.AppendAsync(streamId, baseVersion, envelopes, cancellationToken);
    }

    private EventEnvelope BuildEnvelope(
        StreamId streamId, int streamVersion, AppendedEvent appended, string source, Guid actorId)
    {
        var metadata = new EventMetadata(
            EventId: Guid.NewGuid(),
            CorrelationId: appended.CorrelationId,
            CausationId: Guid.Empty,
            ActorId: actorId,
            Source: source,
            OccurredUtc: appended.OccurredUtc,
            Tenant: WellKnownTenants.Default);

        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: metadata.EventId,
            EventType: appended.Event.GetType().Name,
            EventVersion: _schemaVersions.CurrentVersionFor(appended.Event.GetType().Name),
            Payload: appended.Event,
            Metadata: metadata,
            OccurredUtc: metadata.OccurredUtc,
            GlobalPosition: 0);
    }
}

// One event bound for a stream, carrying the caller's correlation and the time the source recorded.
public sealed record AppendedEvent(IDomainEvent Event, Guid CorrelationId, DateTime OccurredUtc);
