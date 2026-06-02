using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresInventoryDashboardStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "inventory-dashboard";
    private static readonly DateTime At = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public PostgresInventoryDashboardStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateDashboard_inserts_a_row_with_zeroed_quantities()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = await store.GetBySkuAsync("SKU-1", CancellationToken.None);
        row!.InventoryId.Should().Be(inventoryId);
        row.Sku.Should().Be("SKU-1");
        row.OnHandQuantity.Should().Be(0);
        row.ReservedQuantity.Should().Be(0);
    }

    [Fact]
    public async Task CreateDashboard_tolerates_a_duplicate_sku_under_a_different_inventory_id()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();

        // Each create commits in its own transaction, as the projection runs them.
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(firstOwner, "SKU-1", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        // The second create reuses the SKU under a different InventoryId, which the
        // sku UNIQUE constraint would reject. The untargeted ON CONFLICT DO NOTHING
        // makes it a no-op rather than a throw that would fault the projection.
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(secondOwner, "SKU-1", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        // The first SKU owner keeps the row.
        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.InventoryId.Should().Be(firstOwner);
    }

    [Fact]
    public async Task AdjustOnHand_accumulates_in_both_directions()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.AdjustOnHandAsync(inventoryId, 100, At, CancellationToken.None);
            await uow.AdjustOnHandAsync(inventoryId, -30, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.OnHandQuantity.Should().Be(70);
    }

    [Fact]
    public async Task Reserve_then_release_via_recovered_quantity_returns_reserved_to_zero()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.AdjustReservedAsync(inventoryId, 5, At, CancellationToken.None);
            await uow.InsertReservationAsync(
                new InventoryReservationRow(inventoryId, orderId, lineId, 5, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(5);

        // The release path the projection runs: recover the quantity from the
        // lookup, reverse reserved by it, delete the lookup row.
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            var reservation = await uow.GetReservationAsync(
                inventoryId, orderId, lineId, CancellationToken.None);
            await uow.AdjustReservedAsync(inventoryId, -reservation!.Quantity, At, CancellationToken.None);
            await uow.DeleteReservationAsync(inventoryId, orderId, lineId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None))!.ReservedQuantity.Should().Be(0);
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InsertReservation_then_GetReservation_round_trips_by_full_key()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var reservation = new InventoryReservationRow(inventoryId, orderId, lineId, 7, At);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.InsertReservationAsync(reservation, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(inventoryId, orderId, lineId, CancellationToken.None))
            .Should().Be(reservation);
        // A wrong line id misses the full-PK lookup.
        (await read.GetReservationAsync(inventoryId, orderId, Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_returns_rows_ordered_by_sku()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-C", At, CancellationToken.None);
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-A", At, CancellationToken.None);
            await uow.CreateDashboardAsync(Guid.NewGuid(), "SKU-B", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var all = await store.GetAllAsync(CancellationToken.None);
        all.Should().HaveCount(3);
        // ORDER BY sku is the contract; assert the order, not just the count.
        all.Select(r => r.Sku).Should().ContainInOrder("SKU-A", "SKU-B", "SKU-C");
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_the_write_and_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back both
            // the write and the checkpoint advance.
        }

        (await store.GetBySkuAsync("SKU-1", CancellationToken.None)).Should().BeNull();
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(0);
    }

    [Fact]
    public async Task Commit_advances_the_checkpoint_in_the_same_transaction_as_the_write()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresInventoryDashboardStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var inventoryId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateDashboardAsync(inventoryId, "SKU-1", At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 9, CancellationToken.None);
        }

        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(9);
    }
}
