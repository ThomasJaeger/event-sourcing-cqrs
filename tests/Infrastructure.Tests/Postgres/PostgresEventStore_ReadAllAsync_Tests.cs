using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class PostgresEventStore_ReadAllAsync_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresEventStore_ReadAllAsync_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadAll_returns_empty_for_empty_store()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());

        var read = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAll_returns_events_across_streams_in_global_position_order()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamA = Guid.NewGuid();
        var streamB = Guid.NewGuid();

        // Interleave the appends so global order differs from per-stream order.
        await store.AppendAsync(streamA, 0,
            [BuildEnvelope(streamA, 1, new TestPayload(Guid.NewGuid(), 1m))],
            CancellationToken.None);
        await store.AppendAsync(streamB, 0,
            [BuildEnvelope(streamB, 1, new TestPayload(Guid.NewGuid(), 2m))],
            CancellationToken.None);
        await store.AppendAsync(streamA, 1,
            [BuildEnvelope(streamA, 2, new TestPayload(Guid.NewGuid(), 3m))],
            CancellationToken.None);

        var read = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));

        read.Select(e => e.StreamId).Should().Equal(streamA, streamB, streamA);
        read.Select(e => e.StreamVersion).Should().Equal(1, 1, 2);
        read.Select(e => e.GlobalPosition).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadAll_populates_contiguous_global_position_on_each_envelope()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        var read = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));

        // Fresh database, IDENTITY starts at 1, nothing else appended.
        read.Select(e => e.GlobalPosition).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ReadAll_resumes_from_non_zero_from_position_exclusive()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        var read = await CollectAsync(store.ReadAllAsync(1, CancellationToken.None));

        // fromPosition is exclusive: position 1 is skipped, 2 and 3 remain.
        read.Select(e => e.GlobalPosition).Should().Equal(2, 3);
        read.Select(e => e.StreamVersion).Should().Equal(2, 3);
    }

    [Fact]
    public async Task ReadAll_honors_cancellation_mid_enumeration()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var streamId = Guid.NewGuid();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await using var enumerator =
            store.ReadAllAsync(0, cts.Token).GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        cts.Cancel();

        var act = async () => await enumerator.MoveNextAsync();
        await act.Should().ThrowAsync<OperationCanceledException>();
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
