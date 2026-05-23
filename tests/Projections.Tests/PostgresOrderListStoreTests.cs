using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresOrderListStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-list";
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public PostgresOrderListStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Insert_and_commit_persist_the_row_and_advance_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var row = SampleRow(Guid.NewGuid());

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(row, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 5, CancellationToken.None);
        }

        (await store.GetAsync(row.OrderId, CancellationToken.None)).Should().Be(row);
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(5);
    }

    [Fact]
    public async Task Insert_is_idempotent_on_a_conflicting_order_id()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var orderId = Guid.NewGuid();
        var first = SampleRow(orderId);
        var second = first with { CustomerId = Guid.NewGuid(), Total = new Money(1m, Currency.USD) };

        await InsertAndCommitAsync(store, first, position: 1);
        await InsertAndCommitAsync(store, second, position: 2);

        // ON CONFLICT DO NOTHING: the first row stands, the second is discarded.
        (await store.GetAsync(orderId, CancellationToken.None)).Should().Be(first);
    }

    [Fact]
    public async Task UpdateStatus_changes_status_and_last_updated_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var row = SampleRow(Guid.NewGuid());
        await InsertAndCommitAsync(store, row, position: 1);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.UpdateStatusAsync(
                row.OrderId, OrderStatus.Shipped, UpdatedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        var updated = await store.GetAsync(row.OrderId, CancellationToken.None);
        updated!.Status.Should().Be(OrderStatus.Shipped);
        updated.LastUpdatedUtc.Should().Be(UpdatedAt);
        updated.PlacedUtc.Should().Be(PlacedAt);
    }

    [Fact]
    public async Task UpdateStatus_on_an_absent_order_id_affects_no_rows()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var absentId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // No row exists; the update touches zero rows and does not throw.
            await uow.UpdateStatusAsync(
                absentId, OrderStatus.Cancelled, UpdatedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetAsync(absentId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_the_row_write_and_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var row = SampleRow(Guid.NewGuid());

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertAsync(row, CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back.
        }

        (await store.GetAsync(row.OrderId, CancellationToken.None)).Should().BeNull();
        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(0);
    }

    [Fact]
    public async Task GetCheckpointAsync_inside_a_unit_of_work_reads_a_previously_committed_advance()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));

        // Commit an advance to 5 through a first uow.
        await using (var first = await store.BeginAsync(CancellationToken.None))
        {
            await first.InsertAsync(SampleRow(Guid.NewGuid()), CancellationToken.None);
            await first.CommitAsync(ProjectionName, 5, CancellationToken.None);
        }

        // A second uow's GetCheckpointAsync sees the persisted value: the read
        // joins the new transaction and finds the committed checkpoint row.
        await using var second = await store.BeginAsync(CancellationToken.None);
        var checkpoint = await second.GetCheckpointAsync(ProjectionName, CancellationToken.None);

        checkpoint.Should().Be(5);
    }

    [Fact]
    public async Task GetPageAsync_returns_rows_newest_first_by_placed_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var older = SampleRow(Guid.NewGuid()) with { PlacedUtc = PlacedAt.AddDays(-1) };
        var newer = SampleRow(Guid.NewGuid()) with { PlacedUtc = PlacedAt };
        await InsertAndCommitAsync(store, older, position: 1);
        await InsertAndCommitAsync(store, newer, position: 2);

        var page = await store.GetPageAsync(offset: 0, limit: 50, CancellationToken.None);

        page.Should().HaveCount(2);
        page[0].OrderId.Should().Be(newer.OrderId);
        page[1].OrderId.Should().Be(older.OrderId);
    }

    [Fact]
    public async Task GetPageAsync_applies_offset_and_limit_within_the_ordered_page()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var first = SampleRow(Guid.NewGuid()) with { PlacedUtc = PlacedAt };
        var second = SampleRow(Guid.NewGuid()) with { PlacedUtc = PlacedAt.AddDays(-1) };
        var third = SampleRow(Guid.NewGuid()) with { PlacedUtc = PlacedAt.AddDays(-2) };
        await InsertAndCommitAsync(store, first, position: 1);
        await InsertAndCommitAsync(store, second, position: 2);
        await InsertAndCommitAsync(store, third, position: 3);

        var page = await store.GetPageAsync(offset: 1, limit: 1, CancellationToken.None);

        page.Should().ContainSingle();
        page[0].OrderId.Should().Be(second.OrderId);
    }

    [Fact]
    public async Task Truncate_empties_the_table()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var first = SampleRow(Guid.NewGuid());
        var second = SampleRow(Guid.NewGuid());
        await InsertAndCommitAsync(store, first, position: 1);
        await InsertAndCommitAsync(store, second, position: 2);

        await store.TruncateAsync(CancellationToken.None);

        (await store.GetAsync(first.OrderId, CancellationToken.None)).Should().BeNull();
        (await store.GetAsync(second.OrderId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InsertShipmentMapping_then_GetOrderIdByShipmentId_round_trips()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertShipmentMappingAsync(shipmentId, orderId, PlacedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None))
            .Should().Be(orderId);
    }

    [Fact]
    public async Task GetOrderIdByShipmentId_returns_null_when_unmapped()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));

        await using var uow = await store.BeginAsync(CancellationToken.None);
        (await uow.GetOrderIdByShipmentIdAsync(Guid.NewGuid(), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task MarkReturned_sets_is_returned_and_returned_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var row = SampleRow(Guid.NewGuid());
        await InsertAndCommitAsync(store, row, position: 1);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.MarkReturnedAsync(row.OrderId, UpdatedAt, UpdatedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        var updated = await store.GetAsync(row.OrderId, CancellationToken.None);
        updated!.IsReturned.Should().BeTrue();
        updated.ReturnedUtc.Should().Be(UpdatedAt);
        // The Sales status is untouched: a return is a Fulfillment fact (D5).
        updated.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public async Task Truncate_also_empties_the_shipment_mapping()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var shipmentId = Guid.NewGuid();
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertShipmentMappingAsync(
                shipmentId, Guid.NewGuid(), PlacedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await store.TruncateAsync(CancellationToken.None);

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetPageAsync_surfaces_return_state_for_a_returned_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderListStore(factory, new PostgresCheckpointStore(factory));
        var row = SampleRow(Guid.NewGuid());
        await InsertAndCommitAsync(store, row, position: 1);
        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.MarkReturnedAsync(row.OrderId, UpdatedAt, UpdatedAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        var page = await store.GetPageAsync(offset: 0, limit: 50, CancellationToken.None);

        // The page-read path maps is_returned/returned_utc, not just the
        // single-row GetAsync; the other GetPageAsync tests exercise the
        // IsReturned = false default only, so this locks the true/non-null case.
        page.Should().ContainSingle();
        page[0].IsReturned.Should().BeTrue();
        page[0].ReturnedUtc.Should().Be(UpdatedAt);
    }

    private static async Task InsertAndCommitAsync(
        PostgresOrderListStore store, OrderListRow row, long position)
    {
        await using var uow = await store.BeginAsync(CancellationToken.None);
        await uow.InsertAsync(row, CancellationToken.None);
        await uow.CommitAsync(ProjectionName, position, CancellationToken.None);
    }

    private static OrderListRow SampleRow(Guid orderId)
        => new(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            Total: new Money(149.95m, Currency.USD),
            PlacedUtc: PlacedAt,
            LastUpdatedUtc: PlacedAt,
            IsReturned: false,
            ReturnedUtc: null);
}
