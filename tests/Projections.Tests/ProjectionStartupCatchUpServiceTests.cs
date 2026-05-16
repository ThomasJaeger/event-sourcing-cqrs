using System.Data.Common;
using System.Runtime.CompilerServices;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Projections.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class ProjectionStartupCatchUpServiceTests
{
    private static readonly DateTime At = new(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task StartingAsync_replays_each_registered_projection()
    {
        var pA = new RecordingProjection("A");
        var pB = new RecordingProjection("B");
        var checkpoints = new InMemoryCheckpointStore();
        var store = new FakeEventStore(
        [
            Envelope(new TestEvent("e1"), 1),
            Envelope(new TestEvent("e2"), 2),
            Envelope(new TestEvent("e3"), 3),
        ]);
        var service = new ProjectionStartupCatchUpService(
            [pA, pB],
            store,
            checkpoints,
            NullLogger<ProjectionStartupCatchUpService>.Instance);

        await service.StartingAsync(CancellationToken.None);

        pA.Received.Select(c => c.Event.Note).Should().Equal("e1", "e2", "e3");
        pB.Received.Select(c => c.Event.Note).Should().Equal("e1", "e2", "e3");
    }

    [Fact]
    public async Task StartingAsync_uses_per_projection_checkpoints()
    {
        var pA = new RecordingProjection("A");
        var pB = new RecordingProjection("B");
        var checkpoints = new InMemoryCheckpointStore();
        // A has never checkpointed (0); B has processed up to position 5.
        checkpoints.Seed("B", 5);
        var store = new FakeEventStore(
        [
            Envelope(new TestEvent("e1"), 1),
            Envelope(new TestEvent("e5"), 5),
            Envelope(new TestEvent("e6"), 6),
            Envelope(new TestEvent("e7"), 7),
        ]);
        var service = new ProjectionStartupCatchUpService(
            [pA, pB],
            store,
            checkpoints,
            NullLogger<ProjectionStartupCatchUpService>.Instance);

        await service.StartingAsync(CancellationToken.None);

        pA.Received.Select(c => c.Event.Note).Should().Equal("e1", "e5", "e6", "e7");
        // B's checkpoint at 5 is exclusive: ReadAllAsync(5) yields positions > 5.
        pB.Received.Select(c => c.Event.Note).Should().Equal("e6", "e7");
    }

    [Fact]
    public async Task StartingAsync_propagates_cancellation()
    {
        var projection = new RecordingProjection("A");
        var checkpoints = new InMemoryCheckpointStore();
        var store = new FakeEventStore([Envelope(new TestEvent("e1"), 1)]);
        var service = new ProjectionStartupCatchUpService(
            [projection],
            store,
            checkpoints,
            NullLogger<ProjectionStartupCatchUpService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await service.StartingAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static EventEnvelope Envelope(IDomainEvent payload, long globalPosition)
    {
        var id = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: id,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At);
        return new EventEnvelope(
            StreamId: Guid.NewGuid(),
            StreamVersion: 1,
            EventId: id,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: At,
            GlobalPosition: globalPosition);
    }

    // Public so the replayer's reflection can construct
    // EventContext<TestEvent> across the assembly boundary.
    public sealed record TestEvent(string Note) : IDomainEvent;

    public sealed class RecordingProjection(string name)
        : IProjection, IEventHandler<TestEvent>
    {
        public string Name { get; } = name;

        public List<EventContext<TestEvent>> Received { get; } = [];

        public Task HandleAsync(EventContext<TestEvent> context, CancellationToken ct)
        {
            Received.Add(context);
            return Task.CompletedTask;
        }
    }

    // Yields events in global_position order, applying the same exclusive
    // fromPosition filter the real PostgresEventStore.ReadAllAsync applies.
    // Append and stream reads are not exercised by the catch-up service.
    private sealed class FakeEventStore : IEventStore
    {
        private readonly IReadOnlyList<EventEnvelope> _events;

        public FakeEventStore(IReadOnlyList<EventEnvelope> events) => _events = events;

        public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
            long fromPosition, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var envelope in _events)
            {
                if (envelope.GlobalPosition > fromPosition)
                {
                    yield return envelope;
                }
            }
        }

        public Task AppendAsync(
            Guid streamId, int expectedVersion,
            IReadOnlyList<EventEnvelope> events, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
            Guid streamId, int fromVersion = 0, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // Simple dictionary-backed checkpoint store. The catch-up service uses
    // only the non-transactional GetPositionAsync; the other two methods
    // satisfy the interface but are never reached by these tests.
    private sealed class InMemoryCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<string, long> _positions = [];

        public void Seed(string projectionName, long position)
            => _positions[projectionName] = position;

        public Task<long> GetPositionAsync(string projectionName, CancellationToken ct)
            => Task.FromResult(_positions.GetValueOrDefault(projectionName));

        public Task<long> GetPositionAsync(
            string projectionName, DbTransaction transaction, CancellationToken ct)
            => GetPositionAsync(projectionName, ct);

        public Task AdvanceAsync(
            string projectionName, long position,
            DbTransaction transaction, CancellationToken ct)
        {
            _positions[projectionName] = Math.Max(
                _positions.GetValueOrDefault(projectionName), position);
            return Task.CompletedTask;
        }
    }
}
