using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Phase 12 operational reader, commit 1: the head-position read over
// event_store.events. The head is the maximum assigned global_position, and 0
// for an empty store. PostgresEventStoreHeadReader does not exist yet; this test
// drives it into being, constructed over the event-store connection factory
// exactly as PostgresEventStore is in the sibling tests.
public class PostgresEventStoreHeadReader_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStoreHeadReader_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetHeadPosition_returns_zero_for_an_empty_store()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var reader = new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource));

        var head = await reader.GetHeadPositionAsync(CancellationToken.None);

        head.Should().Be(0);
    }

    [Fact]
    public async Task GetHeadPosition_returns_the_max_global_position_after_appends()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamA = NewStreamId();
        var streamB = NewStreamId();

        // Interleave appends across two streams so the head is a global property,
        // not a per-stream one, mirroring the ReadAllAsync sibling test.
        await store.AppendAsync(streamA, 0,
            [BuildEnvelope(streamA, 1, new TestPayload(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        await store.AppendAsync(streamB, 0,
            [BuildEnvelope(streamB, 1, new TestPayload(Guid.NewGuid(), 2m))],
            CancellationToken.None);
        await store.AppendAsync(streamA, 1,
            [BuildEnvelope(streamA, 2, new TestPayload(Guid.NewGuid(), 3m))],
            CancellationToken.None);

        var appended = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));
        var expectedMaxGlobalPosition = appended.Max(e => e.GlobalPosition);

        var reader = new PostgresEventStoreHeadReader(new NpgsqlConnectionFactory(dataSource));

        var head = await reader.GetHeadPositionAsync(CancellationToken.None);

        head.Should().Be(expectedMaxGlobalPosition);
    }

    private static async Task<List<EventEnvelope>> CollectAsync(
        IAsyncEnumerable<EventEnvelope> source)
    {
        var result = new List<EventEnvelope>();
        await foreach (var envelope in source)
        {
            result.Add(envelope);
        }
        return result;
    }
}
