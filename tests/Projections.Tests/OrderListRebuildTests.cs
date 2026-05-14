using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderList;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        liveB!.Status.Should().Be(OrderStatus.Cancelled);
        liveC!.Status.Should().Be(OrderStatus.Placed);
        liveCheckpoint.Should().Be(11);

        // Truncate the read model and clear the checkpoint, then rebuild from zero.
        await ctx.OrderListStore.TruncateAsync(CancellationToken.None);
        await ClearCheckpointAsync(connStr);
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection)
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

        // Order A occupies global positions 1-4. Replaying from 4 skips it
        // entirely and applies only orders B and C.
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection)
            .ReplayAsync(4, CancellationToken.None);

        (await ctx.OrderListStore.GetAsync(ctx.OrderA, CancellationToken.None)).Should().BeNull();
        (await ctx.OrderListStore.GetAsync(ctx.OrderB, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Cancelled);
        (await ctx.OrderListStore.GetAsync(ctx.OrderC, CancellationToken.None))!
            .Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public async Task Replay_run_twice_is_idempotent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);
        var replayer = new ProjectionReplayer(ctx.EventStore, ctx.Projection);

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
    // added, placed). Global positions land 1-4, 5-8, 9-11 in that order.
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreateJsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var orderListStore = new PostgresOrderListStore(readModelFactory, checkpointStore);
        var projection = new OrderListProjection(orderListStore);

        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var orderC = Guid.NewGuid();
        var customer = Guid.NewGuid();

        await eventStore.AppendAsync(orderA, 0,
        [
            Env(orderA, 1, new OrderDrafted(orderA, customer, BaseTime)),
            Env(orderA, 2, new OrderLineAdded(
                orderA, Guid.NewGuid(), "SKU-A", 1, new Money(20m, "USD"), BaseTime)),
            Env(orderA, 3, new OrderPlaced(
                orderA, customer, new Money(20m, "USD"), BaseTime.AddHours(1))),
            Env(orderA, 4, new OrderShipped(orderA, "UPS", "1Z-A", BaseTime.AddHours(2))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(orderB, 0,
        [
            Env(orderB, 1, new OrderDrafted(orderB, customer, BaseTime)),
            Env(orderB, 2, new OrderLineAdded(
                orderB, Guid.NewGuid(), "SKU-B", 3, new Money(5m, "USD"), BaseTime)),
            Env(orderB, 3, new OrderPlaced(
                orderB, customer, new Money(15m, "USD"), BaseTime.AddHours(1))),
            Env(orderB, 4, new OrderCancelled(
                orderB, "out of stock", Guid.NewGuid(), BaseTime.AddHours(2))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(orderC, 0,
        [
            Env(orderC, 1, new OrderDrafted(orderC, customer, BaseTime)),
            Env(orderC, 2, new OrderLineAdded(
                orderC, Guid.NewGuid(), "SKU-C", 2, new Money(49.50m, "USD"), BaseTime)),
            Env(orderC, 3, new OrderPlaced(
                orderC, customer, new Money(99m, "USD"), BaseTime.AddHours(1))),
        ], CancellationToken.None);

        return new RebuildContext(
            eventStore, checkpointStore, orderListStore, projection, orderA, orderB, orderC);
    }

    private static EventEnvelope Env(Guid streamId, int version, IDomainEvent payload)
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

    private static InProcessMessageDispatcher BuildDispatcher(OrderListProjection projection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton<IEventHandler<OrderShipped>>(projection);
        services.AddSingleton<IEventHandler<OrderCancelled>>(projection);
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
            .Register<OrderCancelled>();

    private static JsonSerializerOptions CreateJsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
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
