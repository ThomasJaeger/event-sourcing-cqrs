using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application;

// Engine-agnostic repository wrapper. Loads by replaying the stream through
// the aggregate's ApplyHistoric path; saves by dequeueing uncommitted events,
// stamping each with metadata from the current command context, and appending
// atomically through IEventStore.
//
// Moved here from Infrastructure/EventStore.InMemory in commit 8, closing the
// Session 0006 deferred-items #11 / Session 0007 deferred-items #1 placement.
// Every dependency the class touches (IEventStore, ICommandContextAccessor,
// EventMetadata, EventEnvelope, AggregateRoot) lives in Domain.Abstractions,
// so the class's natural home is Application alongside the command handlers
// that use it.
public sealed class EventStoreRepository<TAggregate> : IEventStoreRepository<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    private readonly IEventStore _store;
    private readonly ICommandContextAccessor _accessor;

    public EventStoreRepository(IEventStore store, ICommandContextAccessor accessor)
    {
        _store = store;
        _accessor = accessor;
    }

    public async Task<TAggregate?> LoadAsync(Guid id, CancellationToken ct)
    {
        var streamId = StreamId.ForAggregate<TAggregate>(id);
        var envelopes = await _store.ReadStreamAsync(streamId, fromVersion: 0, ct);
        if (envelopes.Count == 0)
        {
            return null;
        }

        var aggregate = new TAggregate();
        foreach (var envelope in envelopes)
        {
            aggregate.ApplyHistoric(envelope.Payload);
        }
        return aggregate;
    }

    public async Task SaveAsync(TAggregate aggregate, CancellationToken ct)
    {
        var events = aggregate.DequeueUncommittedEvents();
        if (events.Count == 0)
        {
            return;
        }

        var expectedVersion = aggregate.Version - events.Count;
        var streamId = StreamId.ForAggregate<TAggregate>(aggregate.Id);
        var envelopes = BuildEnvelopes(streamId, expectedVersion, events);
        await _store.AppendAsync(streamId, expectedVersion, envelopes, ct);
    }

    // Stamps metadata from the current command context, chaining causation
    // across multiple events in the same SaveAsync batch (the first event is
    // caused by the command's CausationCommandId; each subsequent event is
    // caused by the prior event's EventId per Ch 8 line 1066).
    //
    // The no-command-in-flight fallback (accessor.Current is null) stamps
    // Guid.Empty placeholders and "Workers" as source. The path exists for
    // tests that construct EventStoreRepository directly without a command
    // bus on the call stack; production writes always go through the bus.
    private IReadOnlyList<EventEnvelope> BuildEnvelopes(
        StreamId streamId,
        int baseVersion,
        IReadOnlyList<IDomainEvent> events)
    {
        var context = _accessor.Current;
        var envelopes = new EventEnvelope[events.Count];
        EventMetadata? previous = null;
        for (int i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            EventMetadata metadata;
            if (previous is null)
            {
                metadata = context is null
                    ? BuildFallbackMetadata()
                    : EventMetadata.ForCommand(context, schemaVersion: 1);
            }
            else
            {
                var occurredUtc = context?.UtcNow().UtcDateTime ?? DateTime.UtcNow;
                metadata = previous.ForCausedEvent(occurredUtc, schemaVersion: 1);
            }
            envelopes[i] = new EventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: metadata.EventId,
                EventType: @event.GetType().Name,
                EventVersion: 1,
                Payload: @event,
                Metadata: metadata,
                OccurredUtc: metadata.OccurredUtc,
                GlobalPosition: 0);
            previous = metadata;
        }
        return envelopes;
    }

    private static EventMetadata BuildFallbackMetadata()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.Empty,
            CausationId: Guid.Empty,
            ActorId: Guid.Empty,
            Source: "Workers",
            SchemaVersion: 1,
            OccurredUtc: DateTime.UtcNow);
}
