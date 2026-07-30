using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;

namespace EventSourcingCqrs.Application;

// The snapshotting repository (Chapter 12): a drop-in IEventStoreRepository that loads a memento and
// replays only the tail from its version, and captures a fresh memento when a save crosses a snapshot
// interval. It wraps the same append path EventStoreRepository uses; the snapshot is a cache over full
// replay, never a source of truth, so a missing or schema-mismatched memento falls back to a full
// replay with no error. Constrained to aggregates that expose the snapshot seam
// (ISnapshotSource<TSnapshot>), so it is registered per root only where a memento type exists.
public sealed class SnapshottingEventStoreRepository<TAggregate, TSnapshot> : IEventStoreRepository<TAggregate>
    where TAggregate : AggregateRoot, ISnapshotSource<TSnapshot>, new()
{
    private readonly IEventStore _store;
    private readonly ICommandContextAccessor _accessor;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private readonly ICurrentEventSchemaVersions _currentVersions;
    private readonly ISnapshotStore _snapshotStore;
    private readonly int _snapshotSchemaVersion;
    private readonly ILogger<SnapshottingEventStoreRepository<TAggregate, TSnapshot>> _logger;
    private readonly int _snapshotInterval;

    // The directive lists the interval before the logger; C# requires the defaulted parameter to be
    // last, so the logger precedes the interval here. Nothing else about the dependency set moves.
    public SnapshottingEventStoreRepository(
        IEventStore store,
        ICommandContextAccessor accessor,
        ICurrentTenantAccessor tenantAccessor,
        ICurrentEventSchemaVersions currentVersions,
        ISnapshotStore snapshotStore,
        int snapshotSchemaVersion,
        ILogger<SnapshottingEventStoreRepository<TAggregate, TSnapshot>> logger,
        int snapshotInterval = 50)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        ArgumentNullException.ThrowIfNull(currentVersions);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(snapshotInterval);
        _store = store;
        _accessor = accessor;
        _tenantAccessor = tenantAccessor;
        _currentVersions = currentVersions;
        _snapshotStore = snapshotStore;
        _snapshotSchemaVersion = snapshotSchemaVersion;
        _logger = logger;
        _snapshotInterval = snapshotInterval;
    }

    // Ask the snapshot store at the schema version this repository was composed for. A hit seats a
    // pristine aggregate at the memento's version and replays only the tail after it; a miss (absent, or
    // a schema the caller cannot consume) is a full replay from version 0, byte-for-byte the base's read.
    public async Task<TAggregate?> LoadAsync(Guid id, CancellationToken ct)
    {
        var streamId = StreamId.ForAggregate<TAggregate>(ResolveTenant(), id);
        var snapshot = await _snapshotStore.LoadAsync<TSnapshot>(streamId, _snapshotSchemaVersion, ct);
        if (snapshot is null)
        {
            return await FullReplayAsync(streamId, ct);
        }

        var aggregate = new TAggregate();
        aggregate.RestoreFrom(snapshot.Snapshot, snapshot.StreamVersion);
        var tail = await _store.ReadStreamAsync(streamId, fromVersion: snapshot.StreamVersion, ct);
        foreach (var envelope in tail)
        {
            aggregate.ApplyHistoric(envelope.Payload);
        }
        return aggregate;
    }

    // Append exactly as the base does, then capture a memento when the append moved the stream into a
    // new snapshot-interval bucket. The append is durable before the capture runs and the capture never
    // fails the command: a snapshot is a cache, so a store that is down costs a warning, not the write.
    public async Task SaveAsync(TAggregate aggregate, CancellationToken ct)
    {
        var events = aggregate.DequeueUncommittedEvents();
        if (events.Count == 0)
        {
            return;
        }

        var postVersion = aggregate.Version;
        var expectedVersion = postVersion - events.Count;
        var tenant = ResolveTenant();
        var streamId = StreamId.ForAggregate<TAggregate>(tenant, aggregate.Id);
        var envelopes = BuildEnvelopes(streamId, expectedVersion, events, tenant);
        await _store.AppendAsync(streamId, expectedVersion, envelopes, ct);

        if (postVersion / _snapshotInterval > expectedVersion / _snapshotInterval)
        {
            await CaptureAsync(streamId, aggregate, postVersion, ct);
        }
    }

    // Best-effort capture (Chapter 12): the append above already committed, so a snapshot failure is
    // logged and swallowed rather than surfaced. Cancellation is not a snapshot failure and still
    // propagates, so a cancelled command does not silently continue.
    private async Task CaptureAsync(StreamId streamId, TAggregate aggregate, int postVersion, CancellationToken ct)
    {
        try
        {
            await _snapshotStore.SaveAsync(
                streamId, aggregate.ToSnapshot(), postVersion, _snapshotSchemaVersion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Snapshot capture failed for stream {StreamId} at version {Version}; the append is "
                + "durable and the command succeeds.",
                streamId.Value,
                postVersion);
        }
    }

    private async Task<TAggregate?> FullReplayAsync(StreamId streamId, CancellationToken ct)
    {
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

    // Duplicated from EventStoreRepository rather than shared: that class is sealed with private
    // helpers, so reusing this write path would mean widening its surface (an internal seam or an
    // extracted collaborator plus a refactor of the base). Per the directive's fallback the write path
    // is duplicated here and flagged; a later slice can extract a shared envelope factory both call.
    private TenantId ResolveTenant()
        => _tenantAccessor.Current
            ?? (_accessor.Current is null ? WellKnownTenants.Default : throw new MissingTenantContextException());

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
                    : EventMetadata.ForCommand(context, tenant);
            }
            else
            {
                var occurredUtc = context?.UtcNow().UtcDateTime ?? DateTime.UtcNow;
                metadata = previous.ForCausedEvent(occurredUtc);
            }
            envelopes[i] = new EventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: metadata.EventId,
                EventType: @event.GetType().Name,
                EventVersion: _currentVersions.CurrentVersionFor(@event.GetType().Name),
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
            OccurredUtc: DateTime.UtcNow,
            Tenant: WellKnownTenants.Default);
}
