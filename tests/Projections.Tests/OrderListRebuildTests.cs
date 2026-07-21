using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The rebuild narrative arc from Chapter 13. Append a realistic event stream,
// drive it through the live dispatcher into the projection, capture the
// read-model state, truncate the read model and clear the checkpoint, replay
// from the event store, and assert the replayed state matches the live one.
// Live dispatch and replay are two drivers of the same handler code; the
// assertion is that they converge.
public class OrderListRebuildTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-list";
    private static readonly DateTime BaseTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public OrderListRebuildTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Replay_from_zero_reproduces_the_live_dispatched_read_model()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);

        // Live pass: every event through the outbox dispatcher into the projection.
        var canonical = await ReadAllAsync(ctx.EventStore);
        var dispatcher = BuildDispatcher(ctx.Projection);
        foreach (var envelope in canonical)
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        var liveA = await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None);
        var liveB = await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None);
        var liveC = await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None);
        var liveCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // The live pass produced the expected state, so the equality assertion
        // below is not vacuously true against two empty read models.
        liveA!.Status.Should().Be(OrderStatus.Shipped);
        // Order A shipped, then its shipment was returned: the status stays
        // Shipped (a Sales fact) and is_returned composes the Fulfillment fact.
        liveA.IsReturned.Should().BeTrue();
        liveB!.Status.Should().Be(OrderStatus.Cancelled);
        liveC!.Status.Should().Be(OrderStatus.Completed);
        liveCheckpoint.Should().Be(14);

        // Truncate the read model and clear the checkpoint, then rebuild from zero.
        await ctx.OrderListStore.TruncateAsync(CancellationToken.None);
        await ClearCheckpointAsync(connStr);
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection, new StubTenantAccessor { Current = WellKnownTenants.Default })
            .ReplayAsync(0, CancellationToken.None);

        (await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None)).Should().Be(liveA);
        (await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None)).Should().Be(liveB);
        (await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None)).Should().Be(liveC);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(liveCheckpoint);
    }

    [Fact]
    public async Task Replay_from_a_checkpoint_applies_only_the_events_after_it()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);

        // Order A occupies global positions 1-4. Replaying from 4 skips A's own
        // events; B, C (with its completion), and A's shipment events still
        // apply. A's ShipmentReturned resolves order A but finds no row to mark,
        // because A's OrderPlaced was skipped.
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection, new StubTenantAccessor { Current = WellKnownTenants.Default })
            .ReplayAsync(4, CancellationToken.None);

        (await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None)).Should().BeNull();
        (await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Cancelled);
        // C is placed (9-11) then completed (12); replaying from 4 applies both.
        (await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Completed);
    }

    [Fact]
    public async Task Replay_run_twice_is_idempotent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);
        var replayer = new ProjectionReplayer(ctx.EventStore, ctx.Projection, new StubTenantAccessor { Current = WellKnownTenants.Default });

        await replayer.ReplayAsync(0, CancellationToken.None);
        var firstA = await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None);
        var firstCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // ON CONFLICT DO NOTHING on insert and GREATEST on the checkpoint mean a
        // second full replay leaves the read model and the checkpoint unchanged.
        await replayer.ReplayAsync(0, CancellationToken.None);

        (await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None)).Should().Be(firstA);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(firstCheckpoint);
    }

    // Builds the stores and the projection over the given data source, then
    // appends three orders' worth of events: A (drafted, line added, placed,
    // shipped), B (drafted, line added, placed, cancelled), C (drafted, line
    // added, placed). Global positions land 1-4, 5-8, 9-11 in that order. Then
    // C completes (position 12) and order A's shipment is scheduled and returned
    // on its own stream (positions 13-14), exercising the completion handler and
    // the ShipmentScheduled-mapping-then-ShipmentReturned-resolve flow.
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions(), new EventUpcasterPipeline(CreateRegistry(), []));
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var orderListStore = new PostgresOrderListStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var projection = new OrderListProjection(orderListStore, NullLogger<OrderListProjection>.Instance);

        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var orderC = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var streamA = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderA);
        var streamB = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderB);
        var streamC = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderC);

        await eventStore.AppendAsync(streamA, 0,
        [
            Env(streamA, 1, new OrderDrafted(orderA, customer, BaseTime, "web")),
            Env(streamA, 2, new OrderLineAdded(
                orderA, Guid.NewGuid(), "SKU-A", 1, new Money(20m, Currency.USD), BaseTime)),
            Env(streamA, 3, new OrderPlaced(
                orderA, customer, new Money(20m, Currency.USD), BaseTime.AddHours(1))),
            Env(streamA, 4, new OrderShipped(orderA, "UPS", "1Z-A", BaseTime.AddHours(2))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamB, 0,
        [
            Env(streamB, 1, new OrderDrafted(orderB, customer, BaseTime, "web")),
            Env(streamB, 2, new OrderLineAdded(
                orderB, Guid.NewGuid(), "SKU-B", 3, new Money(5m, Currency.USD), BaseTime)),
            Env(streamB, 3, new OrderPlaced(
                orderB, customer, new Money(15m, Currency.USD), BaseTime.AddHours(1))),
            Env(streamB, 4, new OrderCancelled(
                orderB, "out of stock", Guid.NewGuid(), BaseTime.AddHours(2))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamC, 0,
        [
            Env(streamC, 1, new OrderDrafted(orderC, customer, BaseTime, "web")),
            Env(streamC, 2, new OrderLineAdded(
                orderC, Guid.NewGuid(), "SKU-C", 2, new Money(49.50m, Currency.USD), BaseTime)),
            Env(streamC, 3, new OrderPlaced(
                orderC, customer, new Money(99m, Currency.USD), BaseTime.AddHours(1))),
        ], CancellationToken.None);

        // Order C completes (the PM-orchestrated terminal). Position 12; status
        // moves Placed -> Completed.
        await eventStore.AppendAsync(streamC, 3,
        [
            Env(streamC, 4, new OrderCompleted(orderC, BaseTime.AddHours(3))),
        ], CancellationToken.None);

        // Order A's shipment is scheduled then returned, on its own shipment
        // stream. Positions 13-14. ShipmentScheduled records the ShipmentId ->
        // OrderId mapping; ShipmentReturned, carrying only ShipmentId, resolves
        // order A through it and marks it returned (ADR 0020).
        var shipmentA = Guid.NewGuid();
        var shipmentStreamA = StreamId.ForAggregate<Shipment>(WellKnownTenants.Default, shipmentA);
        await eventStore.AppendAsync(shipmentStreamA, 0,
        [
            Env(shipmentStreamA, 1, new ShipmentScheduled(
                shipmentA, orderA,
                new Address("1 Main St", "Smalltown", "12345", "US"), [],
                BaseTime.AddHours(2))),
            Env(shipmentStreamA, 2, new ShipmentReturned(shipmentA, "damaged", BaseTime.AddHours(4))),
        ], CancellationToken.None);

        return new RebuildContext(
            eventStore, checkpointStore, orderListStore, projection, orderA, orderB, orderC);
    }

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
            OccurredUtc: BaseTime,
            Tenant: WellKnownTenants.Default);
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
            EventVersion: envelope.EventVersion,
            Event: envelope.Payload,
            Metadata: envelope.Metadata,
            GlobalPosition: envelope.GlobalPosition,
            AttemptCount: 0);

    private static InProcessMessageDispatcher BuildDispatcher(OrderListProjection projection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton<IEventHandler<OrderShipped>>(projection);
        services.AddSingleton<IEventHandler<OrderCancelled>>(projection);
        services.AddSingleton<IEventHandler<OrderCompleted>>(projection);
        services.AddSingleton<IEventHandler<ShipmentScheduled>>(projection);
        services.AddSingleton<IEventHandler<ShipmentReturned>>(projection);
        services.AddSingleton<ICurrentTenantAccessor>(new StubTenantAccessor { Current = WellKnownTenants.Default });
        return new InProcessMessageDispatcher(services.BuildServiceProvider());
    }

    private static async Task ClearCheckpointAsync(string connStr)
    {
        // Production code never deletes checkpoints; the rebuild test is the
        // only consumer, so the cleanup is a raw DELETE here rather than a
        // test-only method on ICheckpointStore.
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
            .Register<OrderPlaced>()
            .Register<OrderShipped>()
            .Register<OrderCancelled>()
            .Register<OrderCompleted>()
            .Register<ShipmentScheduled>()
            .Register<ShipmentReturned>();

    private static ProcessManagerEventTypeRegistry CreatePmRegistry()
        => new();

    private static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        };

    private sealed record RebuildContext(
        PostgresEventStore EventStore,
        PostgresCheckpointStore CheckpointStore,
        PostgresOrderListStore OrderListStore,
        OrderListProjection Projection,
        Guid OrderA,
        Guid OrderB,
        Guid OrderC);
}
