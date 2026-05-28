using System.Text.Json.Nodes;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresOrderDetailStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-detail";
    private static readonly DateTime At = new(2026, 5, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = At.AddHours(1);

    private readonly PostgresFixture _fixture;

    public PostgresOrderDetailStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateHeader_inserts_a_draft_row_with_null_optionals()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, customerId, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.CustomerId.Should().Be(customerId);
        row.Status.Should().Be(OrderStatus.Draft);
        row.PlacedUtc.Should().BeNull();
        row.Total.Should().BeNull();
        row.ShippingAddress.Should().BeNull();
    }

    [Fact]
    public async Task ApplyPlaced_round_trips_status_total_and_placed_utc()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyPlacedAsync(
                orderId, new Money(125.50m, Currency.USD), At, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.Status.Should().Be(OrderStatus.Placed);
        row.PlacedUtc.Should().Be(At);
        row.Total.Should().Be(new Money(125.50m, Currency.USD));
    }

    [Fact]
    public async Task Lifecycle_transitions_stamp_each_utc_and_advance_status()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyPlacedAsync(orderId, new Money(10m, Currency.USD), At, At, CancellationToken.None);
            await uow.ApplyShippedAsync(orderId, Later, Later, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.Status.Should().Be(OrderStatus.Shipped);
        row.PlacedUtc.Should().Be(At);
        row.ShippedUtc.Should().Be(Later);
        row.LastUpdatedUtc.Should().Be(Later);
    }

    [Fact]
    public async Task SetShippingAddress_round_trips_all_four_fields()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();
        var address = new Address("12 Main St", "Portland", "97201", "US");

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.SetShippingAddressAsync(orderId, address, At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetHeaderAsync(orderId, CancellationToken.None))!.ShippingAddress
            .Should().Be(address);
    }

    [Fact]
    public async Task MarkReturned_sets_returned_utc_and_leaves_status()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.ApplyShippedAsync(orderId, At, At, CancellationToken.None);
            await uow.MarkReturnedAsync(orderId, Later, Later, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var row = (await store.GetHeaderAsync(orderId, CancellationToken.None))!;
        row.ReturnedUtc.Should().Be(Later);
        row.Status.Should().Be(OrderStatus.Shipped);
    }

    [Fact]
    public async Task Lines_insert_and_read_back_as_a_set()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();
        var line1 = new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-1", 2, new Money(5m, Currency.USD));
        var line2 = new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-2", 1, new Money(8.25m, Currency.USD));

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertLineAsync(line1, CancellationToken.None);
            await uow.InsertLineAsync(line2, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetLinesAsync(orderId, CancellationToken.None))
            .Should().BeEquivalentTo([line1, line2]);
    }

    [Fact]
    public async Task Five_table_write_commits_as_one_unit()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.InsertLineAsync(
                new OrderDetailLineRow(orderId, Guid.NewGuid(), "SKU-1", 1, new Money(5m, Currency.USD)),
                CancellationToken.None);
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderDrafted", At, """{"orderId":"x"}"""),
                CancellationToken.None);
            await uow.InsertShipmentMappingAsync(
                new OrderDetailShipmentRow(shipmentId, orderId, At), CancellationToken.None);
            await uow.InsertPaymentMappingAsync(
                new OrderDetailPaymentRow(paymentId, orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        (await store.GetHeaderAsync(orderId, CancellationToken.None)).Should().NotBeNull();
        (await store.GetLinesAsync(orderId, CancellationToken.None)).Should().ContainSingle();
        (await store.GetTimelineAsync(orderId, CancellationToken.None)).Should().ContainSingle();
        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None)).Should().Be(orderId);
        (await read.GetOrderIdByPaymentIdAsync(paymentId, CancellationToken.None)).Should().Be(orderId);
    }

    [Fact]
    public async Task Timeline_payload_round_trips_through_jsonb()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();
        // Keys out of alphabetical order on purpose: jsonb canonicalises them, so a
        // byte comparison would fail even though the data is identical. The read
        // path compares parsed nodes, not strings.
        const string written = """{"zebra":1,"alpha":{"nested":true},"amount":12.5}""";

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 30, "PaymentAuthorized", At, written),
                CancellationToken.None);
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderDrafted", At, "{}"),
                CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 30, CancellationToken.None);
        }

        var timeline = await store.GetTimelineAsync(orderId, CancellationToken.None);
        // ORDER BY global_position is the contract.
        timeline.Select(t => t.GlobalPosition).Should().ContainInOrder(10L, 30L);
        var payloadAt30 = timeline.Single(t => t.GlobalPosition == 30).Payload;
        JsonNode.DeepEquals(JsonNode.Parse(payloadAt30), JsonNode.Parse(written)).Should().BeTrue();
    }

    [Fact]
    public async Task Shipment_mapping_round_trips_and_returns_null_when_absent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var shipmentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertShipmentMappingAsync(
                new OrderDetailShipmentRow(shipmentId, orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None)).Should().Be(orderId);
        (await read.GetOrderIdByShipmentIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Payment_mapping_round_trips_and_returns_null_when_absent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var paymentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.InsertPaymentMappingAsync(
                new OrderDetailPaymentRow(paymentId, orderId, At), CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        await using var read = await store.BeginAsync(CancellationToken.None);
        (await read.GetOrderIdByPaymentIdAsync(paymentId, CancellationToken.None)).Should().Be(orderId);
        (await read.GetOrderIdByPaymentIdAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back_every_write_and_the_checkpoint()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.AppendTimelineAsync(
                new OrderDetailTimelineRow(orderId, 10, "OrderDrafted", At, "{}"), CancellationToken.None);
            // The block exits without CommitAsync: DisposeAsync rolls back the
            // header, the timeline append, and the checkpoint advance together.
        }

        (await store.GetHeaderAsync(orderId, CancellationToken.None)).Should().BeNull();
        (await store.GetTimelineAsync(orderId, CancellationToken.None)).Should().BeEmpty();
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
        var store = new PostgresOrderDetailStore(factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create());
        var orderId = Guid.NewGuid();

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            await uow.CreateHeaderAsync(orderId, Guid.NewGuid(), At, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 9, CancellationToken.None);
        }

        var checkpoint = await new PostgresCheckpointStore(factory)
            .GetPositionAsync(ProjectionName, CancellationToken.None);
        checkpoint.Should().Be(9);
    }
}
