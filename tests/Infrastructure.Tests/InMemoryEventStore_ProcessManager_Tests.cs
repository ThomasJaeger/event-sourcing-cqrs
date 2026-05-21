using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

public class InMemoryEventStore_ProcessManager_Tests
{
    private static readonly StreamId PmStream =
        StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, Guid.NewGuid());
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Append_then_read_round_trips_pm_envelopes()
    {
        var store = new InMemoryEventStore();
        await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 2, 0), CancellationToken.None);

        var read = await store.ReadProcessManagerStreamAsync(PmStream, 0, CancellationToken.None);

        read.Should().HaveCount(2);
        read.Select(e => e.StreamVersion).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Append_throws_ConcurrencyException_on_stale_expected_version()
    {
        var store = new InMemoryEventStore();
        await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 1, 0), CancellationToken.None);

        var act = async () => await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 1, 0), CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task ReadProcessManagerStream_with_from_version_returns_tail()
    {
        var store = new InMemoryEventStore();
        await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 3, 0), CancellationToken.None);

        var read = await store.ReadProcessManagerStreamAsync(PmStream, 2, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].StreamVersion.Should().Be(3);
    }

    [Fact]
    public async Task ReadProcessManagerStream_throws_on_a_non_pm_stream_id()
    {
        var store = new InMemoryEventStore();
        var aggregateStream = StreamId.Parse($"order:{Guid.NewGuid():N}");

        var act = async () =>
            await store.ReadProcessManagerStreamAsync(aggregateStream, 0, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task PM_events_do_not_appear_in_ReadAllAsync()
    {
        var store = new InMemoryEventStore();
        await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 1, 0), CancellationToken.None);

        var all = new List<EventEnvelope>();
        await foreach (var e in store.ReadAllAsync(0, CancellationToken.None))
        {
            all.Add(e);
        }

        all.Should().BeEmpty();
    }

    [Fact]
    public async Task PM_events_share_the_global_position_counter_but_enumerate_separately()
    {
        var store = new InMemoryEventStore();
        var aggregateStream = StreamId.Parse($"order:{Guid.NewGuid():N}");

        // Aggregate event at position 1, PM event at position 2, aggregate at 3.
        await store.AppendAsync(
            aggregateStream, 0, AggregateEnvelopes(aggregateStream, 1, 0), CancellationToken.None);
        await store.AppendProcessManagerEventsAsync(
            PmStream, 0, PmEnvelopes(PmStream, 1, 0), CancellationToken.None);
        await store.AppendAsync(
            aggregateStream, 1, AggregateEnvelopes(aggregateStream, 1, 1), CancellationToken.None);

        var all = new List<EventEnvelope>();
        await foreach (var e in store.ReadAllAsync(0, CancellationToken.None))
        {
            all.Add(e);
        }

        // ReadAllAsync yields the aggregate events at 1 and 3; position 2 went
        // to the PM event, which enumerates only through the PM read path.
        all.Select(e => e.GlobalPosition).Should().Equal(1, 3);
        var pm = await store.ReadProcessManagerStreamAsync(PmStream, 0, CancellationToken.None);
        pm.Should().ContainSingle().Which.GlobalPosition.Should().Be(2);
    }

    private static IReadOnlyList<ProcessManagerEventEnvelope> PmEnvelopes(
        StreamId streamId, int count, int baseVersion)
    {
        var envelopes = new ProcessManagerEventEnvelope[count];
        for (int i = 0; i < count; i++)
        {
            var eventId = Guid.NewGuid();
            envelopes[i] = new ProcessManagerEventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: eventId,
                EventType: nameof(PmTestEvent),
                EventVersion: 1,
                Payload: new PmTestEvent(),
                Metadata: Meta(eventId),
                OccurredUtc: At,
                GlobalPosition: 0);
        }
        return envelopes;
    }

    private static IReadOnlyList<EventEnvelope> AggregateEnvelopes(
        StreamId streamId, int count, int baseVersion)
    {
        var envelopes = new EventEnvelope[count];
        for (int i = 0; i < count; i++)
        {
            var eventId = Guid.NewGuid();
            envelopes[i] = new EventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: eventId,
                EventType: nameof(AggregateTestEvent),
                EventVersion: 1,
                Payload: new AggregateTestEvent(),
                Metadata: Meta(eventId),
                OccurredUtc: At,
                GlobalPosition: 0);
        }
        return envelopes;
    }

    private static EventMetadata Meta(Guid eventId)
        => new(eventId, Guid.Empty, Guid.Empty, Guid.Empty, "test", 1, At);

    private sealed record PmTestEvent : IProcessManagerEvent;
    private sealed record AggregateTestEvent : IDomainEvent;
}
