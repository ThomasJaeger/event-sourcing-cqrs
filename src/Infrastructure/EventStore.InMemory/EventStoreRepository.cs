using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.InMemory;

public sealed class EventStoreRepository<TAggregate> : IEventStoreRepository<TAggregate>
    where TAggregate : AggregateRoot, new()
{
    private readonly IEventStore _store;

    public EventStoreRepository(IEventStore store)
    {
        _store = store;
    }

    public async Task<TAggregate?> LoadAsync(Guid id, CancellationToken ct)
    {
        var envelopes = await _store.ReadStreamAsync(id, fromVersion: 0, ct);
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
        var envelopes = BuildEnvelopes(aggregate.Id, expectedVersion, events);
        await _store.AppendAsync(aggregate.Id, expectedVersion, envelopes, ct);
    }

    private static IReadOnlyList<EventEnvelope> BuildEnvelopes(
        Guid streamId,
        int baseVersion,
        IReadOnlyList<IDomainEvent> events)
    {
        var envelopes = new EventEnvelope[events.Count];
        var now = DateTime.UtcNow;
        for (int i = 0; i < events.Count; i++)
        {
            var @event = events[i];
            var eventId = Guid.NewGuid();
            var metadata = new EventMetadata(
                EventId: eventId,
                CorrelationId: Guid.Empty,
                CausationId: Guid.Empty,
                ActorId: Guid.Empty,
                Source: "Domain",
                SchemaVersion: 1,
                OccurredUtc: now);
            envelopes[i] = new EventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: eventId,
                EventType: @event.GetType().Name,
                EventVersion: 1,
                Payload: @event,
                Metadata: metadata,
                OccurredUtc: now);
        }
        return envelopes;
    }
}
