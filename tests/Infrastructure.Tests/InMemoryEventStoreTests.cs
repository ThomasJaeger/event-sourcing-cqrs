// Tests in this file exercise the IEventStore contract without going through any aggregate.
// Envelopes are built inline. Domain-level testing lives in Domain.Tests.

using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.InMemory;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests;

public class InMemoryEventStoreTests
{
    private static readonly Guid StreamId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime At = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AppendAsync_then_ReadStreamAsync_round_trip_preserves_order_and_count()
    {
        var store = new InMemoryEventStore();
        var envelopes = BuildEnvelopes(StreamId, count: 3, baseVersion: 0);

        await store.AppendAsync(StreamId, expectedVersion: 0, envelopes, CancellationToken.None);
        var read = await store.ReadStreamAsync(StreamId, fromVersion: 0, CancellationToken.None);

        read.Should().HaveCount(3);
        read.Select(e => e.StreamVersion).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task AppendAsync_throws_ConcurrencyException_on_stale_expectedVersion()
    {
        var store = new InMemoryEventStore();
        var first = BuildEnvelopes(StreamId, count: 1, baseVersion: 0);
        var second = BuildEnvelopes(StreamId, count: 1, baseVersion: 0);
        await store.AppendAsync(StreamId, expectedVersion: 0, first, CancellationToken.None);

        var act = async () =>
            await store.AppendAsync(StreamId, expectedVersion: 0, second, CancellationToken.None);
        var ex = (await act.Should().ThrowAsync<ConcurrencyException>()).Which;

        ex.StreamId.Should().Be(StreamId);
        ex.ExpectedVersion.Should().Be(0);
        ex.ActualVersion.Should().Be(1);
    }

    [Fact]
    public async Task ReadStreamAsync_with_fromVersion_returns_tail()
    {
        var store = new InMemoryEventStore();
        await store.AppendAsync(
            StreamId,
            expectedVersion: 0,
            BuildEnvelopes(StreamId, count: 3, baseVersion: 0),
            CancellationToken.None);

        var read = await store.ReadStreamAsync(StreamId, fromVersion: 2, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].StreamVersion.Should().Be(3);
    }

    private static IReadOnlyList<EventEnvelope> BuildEnvelopes(Guid streamId, int count, int baseVersion)
    {
        var envelopes = new EventEnvelope[count];
        for (int i = 0; i < count; i++)
        {
            var eventId = Guid.NewGuid();
            var metadata = new EventMetadata(
                EventId: eventId,
                CorrelationId: Guid.Empty,
                CausationId: Guid.Empty,
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: At);
            envelopes[i] = new EventEnvelope(
                StreamId: streamId,
                StreamVersion: baseVersion + i + 1,
                EventId: eventId,
                EventType: nameof(TestEvent),
                EventVersion: 1,
                Payload: new TestEvent(),
                Metadata: metadata,
                OccurredUtc: At);
        }
        return envelopes;
    }

    private sealed record TestEvent : IDomainEvent;
}
