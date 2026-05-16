using System.Runtime.CompilerServices;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Projections.Infrastructure;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class ProjectionReplayerTests
{
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Replay_invokes_the_handler_for_each_handled_event()
    {
        var projection = new RecordingProjection();
        var store = new FakeEventStore(
        [
            Envelope(new ReplayTestEvent("one"), globalPosition: 1),
            Envelope(new ReplayTestEvent("two"), globalPosition: 2),
            Envelope(new ReplayTestEvent("three"), globalPosition: 3),
        ]);
        var replayer = new ProjectionReplayer(store, projection);

        await replayer.ReplayAsync(0, CancellationToken.None);

        projection.Received.Select(c => c.Event.Note).Should().Equal("one", "two", "three");
    }

    [Fact]
    public async Task Replay_skips_events_with_no_registered_handler()
    {
        var projection = new RecordingProjection();
        var store = new FakeEventStore(
        [
            Envelope(new ReplayTestEvent("handled"), globalPosition: 1),
            Envelope(new UnhandledTestEvent("ignored"), globalPosition: 2),
            Envelope(new ReplayTestEvent("also handled"), globalPosition: 3),
        ]);
        var replayer = new ProjectionReplayer(store, projection);

        await replayer.ReplayAsync(0, CancellationToken.None);

        // UnhandledTestEvent has no handler on the projection: skipped, not an error.
        projection.Received.Select(c => c.Event.Note).Should().Equal("handled", "also handled");
    }

    [Fact]
    public async Task Replay_builds_the_event_context_from_the_envelope()
    {
        var projection = new RecordingProjection();
        var metadata = Metadata();
        var envelope = new EventEnvelope(
            StreamId: Guid.NewGuid(),
            StreamVersion: 1,
            EventId: metadata.EventId,
            EventType: nameof(ReplayTestEvent),
            EventVersion: 1,
            Payload: new ReplayTestEvent("payload"),
            Metadata: metadata,
            OccurredUtc: At,
            GlobalPosition: 42);
        var replayer = new ProjectionReplayer(new FakeEventStore([envelope]), projection);

        await replayer.ReplayAsync(0, CancellationToken.None);

        var context = projection.Received.Should().ContainSingle().Which;
        context.Event.Should().Be(envelope.Payload);
        context.Metadata.Should().Be(metadata);
        context.GlobalPosition.Should().Be(42);
    }

    [Fact]
    public async Task Replay_passes_from_position_through_to_ReadAllAsync()
    {
        var store = new FakeEventStore([]);
        var replayer = new ProjectionReplayer(store, new RecordingProjection());

        await replayer.ReplayAsync(99, CancellationToken.None);

        store.LastFromPosition.Should().Be(99);
    }

    private static EventEnvelope Envelope(IDomainEvent payload, long globalPosition)
        => new(
            StreamId: Guid.NewGuid(),
            StreamVersion: 1,
            EventId: Guid.NewGuid(),
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: Metadata(),
            OccurredUtc: At,
            GlobalPosition: globalPosition);

    private static EventMetadata Metadata()
        => new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At);

    // Public so the replayer, which lives in another assembly, can reflect over
    // the projection and construct EventContext<ReplayTestEvent> without an
    // accessibility barrier.
    public sealed record ReplayTestEvent(string Note) : IDomainEvent;

    public sealed record UnhandledTestEvent(string Note) : IDomainEvent;

    public sealed class RecordingProjection : IProjection, IEventHandler<ReplayTestEvent>
    {
        public string Name => "recording";

        public List<EventContext<ReplayTestEvent>> Received { get; } = [];

        public Task HandleAsync(EventContext<ReplayTestEvent> context, CancellationToken ct)
        {
            Received.Add(context);
            return Task.CompletedTask;
        }
    }

    // Yields a fixed list in global_position order, applying the same exclusive
    // fromPosition filter the real PostgresEventStore.ReadAllAsync applies, and
    // records the fromPosition it was called with. The append and stream reads
    // are not exercised by the replayer, so they are not supported.
    private sealed class FakeEventStore : IEventStore
    {
        private readonly IReadOnlyList<EventEnvelope> _events;

        public FakeEventStore(IReadOnlyList<EventEnvelope> events) => _events = events;

        public long? LastFromPosition { get; private set; }

        public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
            long fromPosition, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastFromPosition = fromPosition;
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
}
