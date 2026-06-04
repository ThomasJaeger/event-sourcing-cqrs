using System.Runtime.CompilerServices;
using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.InMemory;

public sealed class InMemoryEventStore : IEventStore
{
    private readonly Dictionary<StreamId, List<EventEnvelope>> _streams = [];

    // Append order across all streams. The in-memory store assigns
    // GlobalPosition the way PostgreSQL IDENTITY does: monotonic, from 1. This
    // keeps the teaching scaffolding a faithful model of the real adapter.
    private readonly List<EventEnvelope> _global = [];
    private long _nextGlobalPosition = 1;

    // PM events share the global-position counter with aggregate events
    // (faithful to Postgres's single IDENTITY across the events table) but
    // enumerate separately; _global skips positions assigned to PM events.
    private readonly Dictionary<StreamId, List<ProcessManagerEventEnvelope>> _pmStreams = [];

    public Task AppendAsync(
        StreamId streamId,
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
        StreamId streamId,
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

    public async IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant, long fromPosition, long toPositionInclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // PM events never enter _global, so a scan of _global is already pm-free; filter
        // by the event's tenant and the (fromPosition, toPositionInclusive] window to
        // match the Postgres feed.
        foreach (var envelope in _global)
        {
            if (envelope.GlobalPosition <= fromPosition
                || envelope.GlobalPosition > toPositionInclusive
                || envelope.Metadata.Tenant != tenant)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            yield return envelope;
        }
    }

    public Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
        CancellationToken ct)
    {
        if (!_pmStreams.TryGetValue(streamId, out var stream))
        {
            stream = [];
            _pmStreams[streamId] = stream;
        }

        if (stream.Count != expectedVersion)
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }

        // Draws from the shared counter but stays out of _global, so ReadAllAsync
        // never yields PM events.
        foreach (var envelope in events)
        {
            stream.Add(envelope with { GlobalPosition = _nextGlobalPosition++ });
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        if (!streamId.Value.StartsWith("pm-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ReadProcessManagerStreamAsync requires a process-manager stream id " +
                $"(pm- prefix); got '{streamId}'.",
                nameof(streamId));
        }

        if (!_pmStreams.TryGetValue(streamId, out var stream))
        {
            return Task.FromResult<IReadOnlyList<ProcessManagerEventEnvelope>>(
                Array.Empty<ProcessManagerEventEnvelope>());
        }

        if (fromVersion <= 0)
        {
            return Task.FromResult<IReadOnlyList<ProcessManagerEventEnvelope>>(stream.ToArray());
        }

        return Task.FromResult<IReadOnlyList<ProcessManagerEventEnvelope>>(
            stream.Skip(fromVersion).ToArray());
    }
}
