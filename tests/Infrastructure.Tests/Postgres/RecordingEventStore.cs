using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// A fromVersion-recording IEventStore decorator for the snapshot end-to-end facts: it records the
// fromVersion and the returned event count of each ReadStreamAsync, so a fact can assert the
// snapshotting repository read only the tail from the snapshot's version. Every other member delegates
// straight to the inner store. This is the Infrastructure.Tests sibling of the DynamoDb suite's
// CountingEventStore decorator; the Application.Tests RecordingEventStore is internal to that assembly
// and unreachable here, so the shape is duplicated rather than shared (see the S3 report).
internal sealed class RecordingEventStore : IEventStore
{
    private readonly IEventStore _inner;

    public RecordingEventStore(IEventStore inner)
    {
        _inner = inner;
    }

    public List<int> ReadFromVersions { get; } = [];

    public int? LastReadFromVersion => ReadFromVersions.Count == 0 ? null : ReadFromVersions[^1];

    public int LastReadCount { get; private set; }

    public async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        StreamId streamId, int fromVersion = 0, CancellationToken ct = default)
    {
        ReadFromVersions.Add(fromVersion);
        var events = await _inner.ReadStreamAsync(streamId, fromVersion, ct);
        LastReadCount = events.Count;
        return events;
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
