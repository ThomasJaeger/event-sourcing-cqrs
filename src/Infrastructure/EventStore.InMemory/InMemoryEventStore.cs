using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.InMemory;

public sealed class InMemoryEventStore : IEventStore
{
    private readonly Dictionary<Guid, List<EventEnvelope>> _streams = [];

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
            throw new ConcurrencyException(streamId, expectedVersion, stream.Count);
        }

        stream.AddRange(events);
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
}
