using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresSkuToInventoryIdStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "sku-to-inventory-id";

    private readonly PostgresFixture _fixture;

    public PostgresSkuToInventoryIdStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Record_and_commit_persist_the_mapping_and_advance_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory));
        var inventoryId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync("SKU-1", inventoryId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 5, CancellationToken.None);
        }

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().Be(inventoryId);
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(5);
    }

    [Fact]
    public async Task GetInventoryId_returns_null_for_an_unrecorded_sku()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresSkuToInventoryIdStore(
            new NpgsqlReadModelConnectionFactory(dataSource),
            new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource)));

        (await store.GetInventoryIdAsync("SKU-MISSING", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Record_is_idempotent_on_a_conflicting_sku()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await RecordAndCommitAsync(store, "SKU-1", first, position: 1);
        await RecordAndCommitAsync(store, "SKU-1", second, position: 2);

        // ON CONFLICT DO NOTHING: the first mapping stands, the second is discarded.
        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().Be(first);
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_the_mapping_and_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory));

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync("SKU-1", Guid.NewGuid(), CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back.
        }

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().BeNull();
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(0);
    }

    [Fact]
    public async Task Truncate_empties_the_table()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory));
        await RecordAndCommitAsync(store, "SKU-1", Guid.NewGuid(), position: 1);
        await RecordAndCommitAsync(store, "SKU-2", Guid.NewGuid(), position: 2);

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().BeNull();
        (await store.GetInventoryIdAsync("SKU-2", CancellationToken.None)).Should().BeNull();
    }

    private static async Task RecordAndCommitAsync(
        PostgresSkuToInventoryIdStore store, string sku, Guid inventoryId, long position)
    {
        await using var uow = await store.BeginAsync(CancellationToken.None);
        await uow.RecordAsync(sku, inventoryId, CancellationToken.None);
        await uow.CommitAsync(ProjectionName, position, CancellationToken.None);
    }
}
