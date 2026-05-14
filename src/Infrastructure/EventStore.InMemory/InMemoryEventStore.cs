using System.Runtime.CompilerServices;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.InMemory;

public sealed class InMemoryEventStore : IEventStore
{
    private readonly Dictionary<Guid, List<EventEnvelope>> _streams = [];

    // Append order across all streams. The in-memory store assigns
    // GlobalPosition the way PostgreSQL IDENTITY does: monotonic, from 1. This
    // keeps the teaching scaffolding a faithful model of the real adapter.
    private readonly List<EventEnvelope> _global = [];
    private long _nextGlobalPosition = 1;

    public Task AppendAsync(
        Guid streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
        {
            stream = [];
            _streams[streamId] = stream;
        }

        if (stream.Count != expectedVersion)
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }

        // Write-path envelopes arrive with GlobalPosition 0; the store stamps
        // the real position here so reads return the shape Postgres returns.
        foreach (var envelope in events)
        {
            var positioned = envelope with { GlobalPosition = _nextGlobalPosition++ };
            stream.Add(positioned);
            _global.Add(positioned);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        Guid streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        if (!_streams.TryGetValue(streamId, out var stream))
        {
            return Task.FromResult<IReadOnlyList<EventEnvelope>>(Array.Empty<EventEnvelope>());
        }

        if (fromVersion <= 0)
        {
            return Task.FromResult<IReadOnlyList<EventEnvelope>>(stream.ToArray());
        }

        return Task.FromResult<IReadOnlyList<EventEnvelope>>(stream.Skip(fromVersion).ToArray());
    }

    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // _global is already in ascending GlobalPosition order; skip the prefix
        // at or below fromPosition (exclusive) and yield the rest.
        foreach (var envelope in _global)
        {
            if (envelope.GlobalPosition <= fromPosition)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            yield return envelope;
        }
    }
}
