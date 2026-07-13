using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
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
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());

        var read = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAll_populates_an_ascending_global_position_on_each_envelope()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamId = NewStreamId();
        await store.AppendAsync(streamId, 0,
            [
                BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 1m)),
                BuildEnvelope(streamId, 2, new TestPayload(Guid.NewGuid(), 2m)),
                BuildEnvelope(streamId, 3, new TestPayload(Guid.NewGuid(), 3m)),
            ],
            CancellationToken.None);

        var read = await CollectAsync(store.ReadAllAsync(0, CancellationToken.None));

        // Ascending and populated, never contiguous. The contract is monotonic in commit order
        // and gap-tolerant (ADR 0044), so pinning exact values would over-pin it: a rolled-back
        // append burns a position here, and SQL Server burns identity-cache positions on
        // restart. Zero is the unstored sentinel, so every envelope must read above it.
        read.Should().HaveCount(3);
        read.Select(e => e.GlobalPosition).Should().OnlyContain(position => position > 0);
        read.Select(e => e.GlobalPosition).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ReadAll_honors_cancellation_mid_enumeration()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamId = NewStreamId();
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
