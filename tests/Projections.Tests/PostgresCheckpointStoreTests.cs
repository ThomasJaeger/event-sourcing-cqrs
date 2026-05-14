using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresCheckpointStoreTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresCheckpointStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetPositionAsync_returns_zero_when_no_checkpoint_row_exists()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));

        var position = await store.GetPositionAsync("order-list", CancellationToken.None);

        position.Should().Be(0);
    }

    [Fact]
    public async Task AdvanceAsync_creates_the_checkpoint_row_on_first_advance()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));

        await AdvanceAndCommitAsync(store, dataSource, "order-list", 5);

        var position = await store.GetPositionAsync("order-list", CancellationToken.None);
        position.Should().Be(5);
    }

    [Fact]
    public async Task AdvanceAsync_is_idempotent_when_the_same_position_is_re_advanced()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));

        await AdvanceAndCommitAsync(store, dataSource, "order-list", 7);
        await AdvanceAndCommitAsync(store, dataSource, "order-list", 7);

        var position = await store.GetPositionAsync("order-list", CancellationToken.None);
        position.Should().Be(7);
    }

    [Fact]
    public async Task AdvanceAsync_keeps_the_higher_position_when_a_lower_one_arrives()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource));

        await AdvanceAndCommitAsync(store, dataSource, "order-list", 10);
        // An at-least-once redelivery replays an already-processed position.
        await AdvanceAndCommitAsync(store, dataSource, "order-list", 4);

        var position = await store.GetPositionAsync("order-list", CancellationToken.None);
        position.Should().Be(10);
    }

    // Each advance runs in its own transaction, the way a projection handler
    // would: open a connection, begin a transaction, advance, commit.
    private static async Task AdvanceAndCommitAsync(
        PostgresCheckpointStore store, NpgsqlDataSource dataSource, string name, long position)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await store.AdvanceAsync(name, position, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }
}
