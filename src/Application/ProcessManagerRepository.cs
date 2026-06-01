using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application;

// Engine-agnostic PM repository, parallel to EventStoreRepository. Loads by
// replaying the PM's stream through LoadFromHistory; saves by reading the
// uncommitted events, stamping metadata from the current command context, and
// appending through IEventStore.AppendProcessManagerEventsAsync (which skips
// the outbox per ADR 0013). MarkCommitted runs only after a successful append.
//
// ADR 0012 placed this in Infrastructure; it belongs in Application alongside
// EventStoreRepository, since every dependency lives in Domain.Abstractions.
// The location is an editorial amendment to ADR 0012.
public sealed class ProcessManagerRepository<TPm> : IProcessManagerRepository<TPm>
    where TPm : ProcessManager
{
    private readonly IEventStore _store;
    private readonly ICommandContextAccessor _accessor;

    public ProcessManagerRepository(IEventStore store, ICommandContextAccessor accessor)
    {
        _store = store;
        _accessor = accessor;
    }

    public async Task<TPm?> LoadAsync(
        StreamId streamId, Func<StreamId, TPm> factory, CancellationToken ct)
    {
        var envelopes = await _store.ReadProcessManagerStreamAsync(streamId, fromVersion: 0, ct);
        if (envelopes.Count == 0)
        {
            return null;
        }

        var pm = factory(streamId);
        pm.LoadFromHistory(envelopes.Select(e => e.Payload));
        return pm;
    }

    public async Task<TPm> LoadOrNewAsync(
        StreamId streamId, Func<StreamId, TPm> factory, CancellationToken ct)
        => await LoadAsync(streamId, factory, ct) ?? factory(streamId);

    public async Task SaveAsync(TPm pm, CancellationToken ct)
    {
        var events = pm.GetUncommittedEvents();
        if (events.Count == 0)
        {
            return;
        }

        var expectedVersion = pm.Version - events.Count;
        var envelopes = BuildEnvelopes(pm.StreamId, expectedVersion, events);
        await _store.AppendProcessManagerEventsAsync(pm.StreamId, expectedVersion, envelopes, ct);
        pm.MarkCommitted();
    }

    // Mirrors EventStoreRepository.BuildEnvelopes: the first event is caused by
    // the command context's CausationCommandId, each subsequent event by the
    // prior event's EventId. Fallback to Guid.Empty / "Workers" when no command
    // context is in flight. Propagating causation from the triggering inbound
    // event is a PM-handler concern that lands with the handlers in a later commit.
    private IReadOnlyList<ProcessManagerEventEnvelope> BuildEnvelopes(
        StreamId streamId,
        int baseVersion,
        IReadOnlyList<IProcessManagerEvent> events)
    {
        var context = _accessor.Current;
        var envelopes = new ProcessManagerEventEnvelope[events.Count];
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
            envelopes[i] = new ProcessManagerEventEnvelope(
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
            OccurredUtc: DateTime.UtcNow,
            Tenant: WellKnownTenants.Default);
}
