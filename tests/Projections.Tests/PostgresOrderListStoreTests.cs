using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderList;
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
        var second = first with { CustomerId = Guid.NewGuid(), Total = new Money(1m, "USD") };

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
            Total: new Money(149.95m, "USD"),
            PlacedUtc: PlacedAt,
            LastUpdatedUtc: PlacedAt);
}
