using System.Text.Json;
using System.Text.Json.Nodes;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The rebuild narrative for OrderDetail, the canonical "all sixteen handlers
// compose and replay" test. Three orders reach three different terminals (A
// completed, B cancelled then returned, C shipped), each spanning its own Order,
// Shipment, and Payment streams. The stream interleaves across GlobalPosition so
// order A's shipment-update and payment-capture handlers resolve their lookups
// across a gap (A's ShipmentScheduled and PaymentAuthorized land at positions 7-8,
// the resolving events at 17-19, with order B's events filling the gap). Two
// stranded events (a delivery and a void with no prior mapping) exercise the
// not-found paths. Replay-from-zero reproduces the live-dispatched read model
// row-for-row, replay-run-twice is idempotent, and a focused test proves the JSONB
// payload round-trips end to end.
public class OrderDetailRebuildTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-detail";
    private static readonly DateTime BaseTime = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Address Addr = new("12 Main St", "Portland", "97201", "US");

    private readonly PostgresFixture _fixture;

    public OrderDetailRebuildTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Replay_from_zero_reproduces_the_live_dispatched_read_model()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);

        // Live pass: every event through the dispatcher into the projection.
        var canonical = await ReadAllAsync(ctx.EventStore);
        var dispatcher = BuildDispatcher(ctx.Projection);
        foreach (var envelope in canonical)
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        var live = await SnapshotAsync(ctx);

        // Non-vacuous: each order reached a distinct terminal, and order A's line was
        // removed then re-added, so the live read model is not two empty rows.
        live.HeaderA!.Status.Should().Be(OrderStatus.Completed);
        live.HeaderA.CompletedUtc.Should().NotBeNull();
        live.HeaderA.ShippedUtc.Should().BeNull();
        live.LinesA.Should().ContainSingle().Which.Sku.Should().Be("SKU-A2");
        live.TimelineA.Should().HaveCount(12);
        live.HeaderB!.Status.Should().Be(OrderStatus.Cancelled);
        live.HeaderB.ReturnedUtc.Should().NotBeNull();
        live.TimelineB.Should().HaveCount(8);
        live.HeaderC!.Status.Should().Be(OrderStatus.Shipped);
        live.HeaderC.ShippedUtc.Should().NotBeNull();
        live.HeaderC.CompletedUtc.Should().BeNull();
        live.HeaderC.CancelledUtc.Should().BeNull();
        live.HeaderC.ReturnedUtc.Should().BeNull();
        live.TimelineC.Should().HaveCount(4);
        live.Checkpoint.Should().Be(26);

        await ctx.Store.TruncateAsync(CancellationToken.None);
        await ClearCheckpointAsync(connStr);
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection)
            .ReplayAsync(0, CancellationToken.None);

        var replayed = await SnapshotAsync(ctx);
        replayed.HeaderA.Should().Be(live.HeaderA);
        replayed.LinesA.Should().BeEquivalentTo(live.LinesA);
        replayed.TimelineA.Should().BeEquivalentTo(live.TimelineA);
        replayed.HeaderB.Should().Be(live.HeaderB);
        replayed.LinesB.Should().BeEquivalentTo(live.LinesB);
        replayed.TimelineB.Should().BeEquivalentTo(live.TimelineB);
        replayed.HeaderC.Should().Be(live.HeaderC);
        replayed.LinesC.Should().BeEquivalentTo(live.LinesC);
        replayed.TimelineC.Should().BeEquivalentTo(live.TimelineC);
        replayed.Checkpoint.Should().Be(live.Checkpoint);
    }

    [Fact]
    public async Task Replay_run_twice_is_idempotent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);
        var replayer = new ProjectionReplayer(ctx.EventStore, ctx.Projection);

        await replayer.ReplayAsync(0, CancellationToken.None);
        var first = await SnapshotAsync(ctx);

        // A second full replay re-applies nothing: the skip-guard sees the checkpoint
        // already past every event, so no header double-applies and, critically, no
        // timeline row is appended twice (the (order_id, global_position) PK plus the
        // skip-guard both hold).
        await replayer.ReplayAsync(0, CancellationToken.None);
        var second = await SnapshotAsync(ctx);

        second.HeaderA.Should().Be(first.HeaderA);
        second.TimelineA.Should().BeEquivalentTo(first.TimelineA);
        second.TimelineA.Should().HaveCount(12);
        second.Checkpoint.Should().Be(first.Checkpoint);
    }

    [Fact]
    public async Task Timeline_payload_round_trips_and_deserializes_back_to_the_event()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var store = new PostgresOrderDetailStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory), TestNotificationPublisher.Create());
        var projection = new OrderDetailProjection(
            store, CreateJsonOptions(), NullLogger<OrderDetailProjection>.Instance);

        var orderId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var placed = new OrderPlaced(orderId, customerId, new Money(42.50m, Currency.USD), BaseTime.AddHours(1));
        var stream = StreamId.ForAggregate<Order>(orderId);
        await eventStore.AppendAsync(stream, 0,
        [
            Env(stream, 1, new OrderDrafted(orderId, customerId, BaseTime)),
            Env(stream, 2, placed),
        ], CancellationToken.None);

        var dispatcher = BuildDispatcher(projection);
        foreach (var envelope in await ReadAllAsync(eventStore))
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        var entry = (await store.GetTimelineAsync(orderId, CancellationToken.None))
            .Single(t => t.EventType == nameof(OrderPlaced));
        // PostgreSQL canonicalises jsonb, so compare parsed nodes, not strings.
        JsonNode.DeepEquals(
            JsonNode.Parse(entry.Payload),
            JsonNode.Parse(JsonSerializer.Serialize(placed, CreateJsonOptions())))
            .Should().BeTrue();
        // And the payload deserialises back to the original event, proving the
        // type-token plus serialisation shape end to end.
        JsonSerializer.Deserialize<OrderPlaced>(entry.Payload, CreateJsonOptions()).Should().Be(placed);
    }

    // Order A (completed): drafted, line a1 added then removed, line a2 added,
    // address, placed (pos 1-6); ShipmentScheduled (pos 7); PaymentAuthorized (pos 8).
    // Order B (cancelled, returned, refunded) fills pos 9-16, creating A's gap. Order
    // A then resolves: ShipmentDispatched, ShipmentDelivered (17-18), PaymentCaptured
    // (19), OrderCompleted (20). Order C (shipped): drafted, line c1, placed, shipped
    // (21-24). Stranded: a delivery (25) and a void (26) with no prior mapping.
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var store = new PostgresOrderDetailStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create());
        var projection = new OrderDetailProjection(
            store, CreateJsonOptions(), NullLogger<OrderDetailProjection>.Instance);

        var custA = Guid.NewGuid();
        var orderA = Guid.NewGuid();
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var shipA = Guid.NewGuid();
        var payA = Guid.NewGuid();
        var streamOA = StreamId.ForAggregate<Order>(orderA);
        var streamShipA = StreamId.ForAggregate<Shipment>(shipA);
        var streamPayA = StreamId.ForAggregate<Payment>(payA);

        var custB = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var shipB = Guid.NewGuid();
        var payB = Guid.NewGuid();
        var streamOB = StreamId.ForAggregate<Order>(orderB);
        var streamShipB = StreamId.ForAggregate<Shipment>(shipB);
        var streamPayB = StreamId.ForAggregate<Payment>(payB);

        var custC = Guid.NewGuid();
        var orderC = Guid.NewGuid();
        var c1 = Guid.NewGuid();
        var streamOC = StreamId.ForAggregate<Order>(orderC);

        var money = new Money(50m, Currency.USD);

        // Order A populate (positions 1-8).
        await eventStore.AppendAsync(streamOA, 0,
        [
            Env(streamOA, 1, new OrderDrafted(orderA, custA, BaseTime)),
            Env(streamOA, 2, new OrderLineAdded(orderA, a1, "SKU-A1", 1, money, BaseTime)),
            Env(streamOA, 3, new OrderLineRemoved(orderA, a1, BaseTime)),
            Env(streamOA, 4, new OrderLineAdded(orderA, a2, "SKU-A2", 2, money, BaseTime)),
            Env(streamOA, 5, new ShippingAddressSet(orderA, Addr, BaseTime)),
            Env(streamOA, 6, new OrderPlaced(orderA, custA, money, BaseTime.AddHours(1))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamShipA, 0,
        [
            Env(streamShipA, 1, new ShipmentScheduled(shipA, orderA, Addr, [], BaseTime.AddHours(1))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamPayA, 0,
        [
            Env(streamPayA, 1, new PaymentAuthorized(payA, orderA, money, "auth-A", BaseTime.AddHours(1))),
        ], CancellationToken.None);

        // Order B full (positions 9-16): cancelled, returned, refunded.
        await eventStore.AppendAsync(streamOB, 0,
        [
            Env(streamOB, 1, new OrderDrafted(orderB, custB, BaseTime)),
            Env(streamOB, 2, new OrderLineAdded(orderB, b1, "SKU-B1", 1, money, BaseTime)),
            Env(streamOB, 3, new OrderPlaced(orderB, custB, money, BaseTime.AddHours(1))),
            Env(streamOB, 4, new OrderCancelled(orderB, "out of stock", Guid.NewGuid(), BaseTime.AddHours(2))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamShipB, 0,
        [
            Env(streamShipB, 1, new ShipmentScheduled(shipB, orderB, Addr, [], BaseTime.AddHours(1))),
            Env(streamShipB, 2, new ShipmentReturned(shipB, "damaged", BaseTime.AddHours(3))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamPayB, 0,
        [
            Env(streamPayB, 1, new PaymentAuthorized(payB, orderB, money, "auth-B", BaseTime.AddHours(1))),
            Env(streamPayB, 2, new PaymentRefunded(payB, money, "returned", BaseTime.AddHours(3))),
        ], CancellationToken.None);

        // Order A resolve (positions 17-20), across the gap from positions 7-8.
        await eventStore.AppendAsync(streamShipA, 1,
        [
            Env(streamShipA, 2, new ShipmentDispatched(shipA, "1Z-A", BaseTime.AddHours(2))),
            Env(streamShipA, 3, new ShipmentDelivered(shipA, BaseTime.AddHours(3))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamPayA, 1,
        [
            Env(streamPayA, 2, new PaymentCaptured(payA, money, BaseTime.AddHours(2))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamOA, 6,
        [
            Env(streamOA, 7, new OrderCompleted(orderA, BaseTime.AddHours(4))),
        ], CancellationToken.None);

        // Order C (positions 21-24): shipped terminal.
        await eventStore.AppendAsync(streamOC, 0,
        [
            Env(streamOC, 1, new OrderDrafted(orderC, custC, BaseTime)),
            Env(streamOC, 2, new OrderLineAdded(orderC, c1, "SKU-C1", 1, money, BaseTime)),
            Env(streamOC, 3, new OrderPlaced(orderC, custC, money, BaseTime.AddHours(1))),
            Env(streamOC, 4, new OrderShipped(orderC, "UPS", "1Z-C", BaseTime.AddHours(2))),
        ], CancellationToken.None);

        // Stranded (positions 25-26): no ShipmentScheduled or PaymentAuthorized
        // recorded these, so the handlers resolve null and no-op with a debug log.
        var streamShipX = StreamId.ForAggregate<Shipment>(Guid.NewGuid());
        var streamPayX = StreamId.ForAggregate<Payment>(Guid.NewGuid());
        await eventStore.AppendAsync(streamShipX, 0,
        [
            Env(streamShipX, 1, new ShipmentDelivered(Guid.NewGuid(), BaseTime.AddHours(2))),
        ], CancellationToken.None);
        await eventStore.AppendAsync(streamPayX, 0,
        [
            Env(streamPayX, 1, new PaymentVoided(Guid.NewGuid(), "expired", BaseTime.AddHours(2))),
        ], CancellationToken.None);

        return new RebuildContext(eventStore, checkpointStore, store, projection, orderA, orderB, orderC);
    }

    private static async Task<Snapshot> SnapshotAsync(RebuildContext ctx)
        => new(
            await ctx.Store.GetHeaderAsync(ctx.OrderA, CancellationToken.None),
            await ctx.Store.GetLinesAsync(ctx.OrderA, CancellationToken.None),
            await ctx.Store.GetTimelineAsync(ctx.OrderA, CancellationToken.None),
            await ctx.Store.GetHeaderAsync(ctx.OrderB, CancellationToken.None),
            await ctx.Store.GetLinesAsync(ctx.OrderB, CancellationToken.None),
            await ctx.Store.GetTimelineAsync(ctx.OrderB, CancellationToken.None),
            await ctx.Store.GetHeaderAsync(ctx.OrderC, CancellationToken.None),
            await ctx.Store.GetLinesAsync(ctx.OrderC, CancellationToken.None),
            await ctx.Store.GetTimelineAsync(ctx.OrderC, CancellationToken.None),
            await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None));

    private static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: BaseTime);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: BaseTime,
            GlobalPosition: 0);
    }

    private static async Task<List<EventEnvelope>> ReadAllAsync(IEventStore eventStore)
    {
        var envelopes = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAllAsync(0, CancellationToken.None))
        {
            envelopes.Add(envelope);
        }
        return envelopes;
    }

    private static OutboxMessage ToOutboxMessage(EventEnvelope envelope)
        => new(
            OutboxId: envelope.GlobalPosition,
            EventId: envelope.EventId,
            EventType: envelope.EventType,
            Event: envelope.Payload,
            Metadata: envelope.Metadata,
            GlobalPosition: envelope.GlobalPosition,
            AttemptCount: 0);

    private static InProcessMessageDispatcher BuildDispatcher(OrderDetailProjection projection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<OrderDrafted>>(projection);
        services.AddSingleton<IEventHandler<OrderLineAdded>>(projection);
        services.AddSingleton<IEventHandler<OrderLineRemoved>>(projection);
        services.AddSingleton<IEventHandler<ShippingAddressSet>>(projection);
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton<IEventHandler<OrderShipped>>(projection);
        services.AddSingleton<IEventHandler<OrderCancelled>>(projection);
        services.AddSingleton<IEventHandler<OrderCompleted>>(projection);
        services.AddSingleton<IEventHandler<ShipmentScheduled>>(projection);
        services.AddSingleton<IEventHandler<ShipmentDispatched>>(projection);
        services.AddSingleton<IEventHandler<ShipmentDelivered>>(projection);
        services.AddSingleton<IEventHandler<ShipmentReturned>>(projection);
        services.AddSingleton<IEventHandler<PaymentAuthorized>>(projection);
        services.AddSingleton<IEventHandler<PaymentCaptured>>(projection);
        services.AddSingleton<IEventHandler<PaymentRefunded>>(projection);
        services.AddSingleton<IEventHandler<PaymentVoided>>(projection);
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }

    private static async Task ClearCheckpointAsync(string connStr)
    {
        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM read_models.projection_checkpoints WHERE projection_name = @name";
        cmd.Parameters.AddWithValue("name", ProjectionName);
        await cmd.ExecuteNonQueryAsync();
    }

    private static EventTypeRegistry CreateRegistry()
        => new EventTypeRegistry()
            .Register<OrderDrafted>()
            .Register<OrderLineAdded>()
            .Register<OrderLineRemoved>()
            .Register<ShippingAddressSet>()
            .Register<OrderPlaced>()
            .Register<OrderShipped>()
            .Register<OrderCancelled>()
            .Register<OrderCompleted>()
            .Register<ShipmentScheduled>()
            .Register<ShipmentDispatched>()
            .Register<ShipmentDelivered>()
            .Register<ShipmentReturned>()
            .Register<PaymentAuthorized>()
            .Register<PaymentCaptured>()
            .Register<PaymentRefunded>()
            .Register<PaymentVoided>();

    private static ProcessManagerEventTypeRegistry CreatePmRegistry()
        => new();

    private static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

    private sealed record RebuildContext(
        PostgresEventStore EventStore,
        PostgresCheckpointStore CheckpointStore,
        PostgresOrderDetailStore Store,
        OrderDetailProjection Projection,
        Guid OrderA,
        Guid OrderB,
        Guid OrderC);

    private sealed record Snapshot(
        OrderDetailRow? HeaderA,
        IReadOnlyList<OrderDetailLineRow> LinesA,
        IReadOnlyList<OrderDetailTimelineRow> TimelineA,
        OrderDetailRow? HeaderB,
        IReadOnlyList<OrderDetailLineRow> LinesB,
        IReadOnlyList<OrderDetailTimelineRow> TimelineB,
        OrderDetailRow? HeaderC,
        IReadOnlyList<OrderDetailLineRow> LinesC,
        IReadOnlyList<OrderDetailTimelineRow> TimelineC,
        long Checkpoint);
}
