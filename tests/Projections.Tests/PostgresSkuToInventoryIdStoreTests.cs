using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresSkuToInventoryIdStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "sku-to-inventory-id";

    private static readonly TenantId TenantA =
        TenantId.From(Guid.Parse("a0000000-0000-0000-0000-0000000000a1"));
    private static readonly TenantId TenantB =
        TenantId.From(Guid.Parse("b0000000-0000-0000-0000-0000000000b2"));

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
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
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
            new PostgresCheckpointStore(new NpgsqlReadModelConnectionFactory(dataSource)),
            TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = WellKnownTenants.Default });

        (await store.GetInventoryIdAsync("SKU-MISSING", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Record_is_idempotent_on_a_conflicting_sku()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
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
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });

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
        var store = new PostgresSkuToInventoryIdStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        await RecordAndCommitAsync(store, "SKU-1", Guid.NewGuid(), position: 1);
        await RecordAndCommitAsync(store, "SKU-2", Guid.NewGuid(), position: 2);

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetInventoryIdAsync("SKU-1", CancellationToken.None)).Should().BeNull();
        (await store.GetInventoryIdAsync("SKU-2", CancellationToken.None)).Should().BeNull();
    }

    // Green-with-implementation: the same sku under two tenants resolves to each
    // tenant's own InventoryId, now that the lookup key is (tenant_id, sku) (0018).
    [Fact]
    public async Task GetInventoryIdAsync_resolves_per_tenant_for_the_same_sku()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        const string sku = "SKU-SHARED";
        var inventoryA = Guid.NewGuid();
        var inventoryB = Guid.NewGuid();

        var tenant = new StubTenantAccessor { Current = TenantA };
        var store = new PostgresSkuToInventoryIdStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), tenant);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(sku, inventoryA, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }
        tenant.Current = TenantB;
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.RecordAsync(sku, inventoryB, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        // Each tenant's accessor resolves its own mapping for the shared SKU; the distinct ids
        // mean neither tenant sees the other's.
        tenant.Current = TenantA;
        (await store.GetInventoryIdAsync(sku, CancellationToken.None)).Should().Be(inventoryA);
        tenant.Current = TenantB;
        (await store.GetInventoryIdAsync(sku, CancellationToken.None)).Should().Be(inventoryB);
    }

    private static async Task RecordAndCommitAsync(
        PostgresSkuToInventoryIdStore store, string sku, Guid inventoryId, long position)
    {
        await using var uow = await store.BeginAsync(CancellationToken.None);
        await uow.RecordAsync(sku, inventoryId, CancellationToken.None);
        await uow.CommitAsync(ProjectionName, position, CancellationToken.None);
    }
}
