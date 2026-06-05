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
    private readonly ICurrentTenantAccessor _tenantAccessor;

    public EventStoreRepository(
        IEventStore store, ICommandContextAccessor accessor, ICurrentTenantAccessor tenantAccessor)
    {
        _store = store;
        _accessor = accessor;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<TAggregate?> LoadAsync(Guid id, CancellationToken ct)
    {
        var streamId = StreamId.ForAggregate<TAggregate>(ResolveTenant(), id);
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
        var tenant = ResolveTenant();
        var streamId = StreamId.ForAggregate<TAggregate>(tenant, aggregate.Id);
        var envelopes = BuildEnvelopes(streamId, expectedVersion, events, tenant);
        await _store.AppendAsync(streamId, expectedVersion, envelopes, ct);
    }

    // The tenant is sourced from the accessor, the single source the command bus
    // and the process-manager dispatch loop both set. When it is unset, the
    // no-command-context path falls back to the default tenant (worker writes, the
    // direct-construction tests), while a present command context with no tenant is
    // a dispatch-wiring regression and throws. One resolved tenant feeds both the
    // stream id and the metadata so they cannot disagree.
    private TenantId ResolveTenant()
        => _tenantAccessor.Current
            ?? (_accessor.Current is null ? WellKnownTenants.Default : throw new MissingTenantContextException());

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
        IReadOnlyList<IDomainEvent> events,
        TenantId tenant)
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
                    : EventMetadata.ForCommand(context, tenant, schemaVersion: 1);
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
            OccurredUtc: DateTime.UtcNow,
            Tenant: WellKnownTenants.Default);
}
