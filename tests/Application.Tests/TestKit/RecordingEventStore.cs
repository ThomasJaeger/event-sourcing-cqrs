using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Tests.TestKit;

// Decorates an inner IEventStore and records the fromVersion of each ReadStreamAsync call, so a
// snapshotting-repository fact can assert the repository read only the tail from the snapshot's
// version (or the whole stream from 0 when there is no snapshot). Every other member delegates
// straight through to the inner store, which is the real InMemoryEventStore the existing repository
// facts use; this is a hand-built harness seam over our own port, observing the read position and
// changing no behavior.
internal sealed class RecordingEventStore : IEventStore
{
    private readonly IEventStore _inner;

    public RecordingEventStore(IEventStore inner)
    {
        _inner = inner;
    }

    public List<int> ReadFromVersions { get; } = [];

    public int? LastReadFromVersion => ReadFromVersions.Count == 0 ? null : ReadFromVersions[^1];

    public Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId, int fromVersion = 0, CancellationToken ct = default)
    {
        ReadFromVersions.Add(fromVersion);
        return _inner.ReadStreamAsync(streamId, fromVersion, ct);
    }

    public Task AppendAsync(
        StreamId streamId, int expectedVersion, IReadOnlyList<EventEnvelope> events, CancellationToken ct)
        => _inner.AppendAsync(streamId, expectedVersion, events, ct);

    public Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct)
        => _inner.AppendProcessManagerEventsAsync(streamId, expectedVersion, events, ct);

    public Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId, int fromVersion = 0, CancellationToken ct = default)
        => _inner.ReadProcessManagerStreamAsync(streamId, fromVersion, ct);

    public IAsyncEnumerable<EventEnvelope> ReadAllAsync(long fromPosition, CancellationToken ct = default)
        => _inner.ReadAllAsync(fromPosition, ct);

    public IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant, long fromPosition, long toPositionInclusive, CancellationToken ct = default)
        => _inner.ReadAllForTenantAsync(tenant, fromPosition, toPositionInclusive, ct);
}
