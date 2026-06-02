using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresCustomerSummaryStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "customer-summary";
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LaterAt = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SystemAt = new(2026, 5, 14, 9, 0, 5, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public PostgresCustomerSummaryStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApplyPlacement_inserts_a_row_and_round_trips()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
        row.LastOrderUtc.Should().Be(PlacedAt);
        row.LastUpdatedUtc.Should().Be(SystemAt);
    }

    [Fact]
    public async Task ApplyPlacement_twice_accumulates_and_keeps_the_later_last_order_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // First placement is the later one, second is earlier: GREATEST must
            // keep last_order_utc at the later time rather than regressing it.
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), LaterAt, SystemAt, CancellationToken.None);
            await uow.ApplyPlacementAsync(
                customerId, new Money(30m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(2);
        row.LifetimeValue.Should().Be(new Money(80m, Currency.USD));
        row.LastOrderUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task ApplyCancellation_decrements_and_leaves_last_order_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.ApplyPlacementAsync(
                customerId, new Money(30m, Currency.USD), LaterAt, SystemAt, CancellationToken.None);
            await uow.ApplyCancellationAsync(
                customerId, new Money(30m, Currency.USD), SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 3, CancellationToken.None);
        }

        var row = await store.GetAsync(customerId, CancellationToken.None);
        row!.OrderCount.Should().Be(1);
        row.LifetimeValue.Should().Be(new Money(50m, Currency.USD));
        // last_order_utc is not recomputed on cancellation (ADR 0019).
        row.LastOrderUtc.Should().Be(LaterAt);
    }

    [Fact]
    public async Task Per_order_lookup_round_trips_by_order_id_and_deletes()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderRow = new CustomerSummaryOrderRow(
            customerId, orderId, new Money(40m, Currency.USD), PlacedAt);

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertOrderAsync(orderRow, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // Cancellation looks up by order id alone (no customer id on the event).
            (await uow.GetOrderByOrderIdAsync(orderId, CancellationToken.None)).Should().Be(orderRow);
            await uow.DeleteOrderAsync(customerId, orderId, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 2, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderByOrderIdAsync(orderId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_the_writes()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back.
        }

        (await store.GetAsync(customerId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Commit_advances_the_checkpoint_in_the_same_transaction_as_the_write()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresCustomerSummaryStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.ApplyPlacementAsync(
                customerId, new Money(50m, Currency.USD), PlacedAt, SystemAt, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 9, CancellationToken.None);
        }

        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(9);
    }
}
