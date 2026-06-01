using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The rebuild narrative from Chapter 13, applied to the netting projection.
// Append an interleaved multi-customer stream where one customer places two
// orders and cancels one, drive it live into the projection, capture the
// netted read model, truncate and clear the checkpoint, replay from zero, and
// assert the replayed state matches the live one row-for-row. The cancellation
// lands at global position 10, after both customers' placements, so the netting
// recovers its customer and total from the lookup across a non-trivial gap.
public class CustomerSummaryRebuildTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "customer-summary";
    private static readonly DateTime BaseTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public CustomerSummaryRebuildTests(PostgresFixture fixture)
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

        var liveCust1 = await ctx.Store.GetAsync(ctx.Cust1, CancellationToken.None);
        var liveCust2 = await ctx.Store.GetAsync(ctx.Cust2, CancellationToken.None);
        var liveCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // The live pass produced the netted state, so the equality below is not
        // vacuously true against two empty read models. Cust1 placed two orders
        // (50 + 30) then cancelled the first, netting to one order at 30.
        liveCust1!.OrderCount.Should().Be(1);
        liveCust1.LifetimeValue.Should().Be(new Money(30m, Currency.USD));
        liveCust1.LastOrderUtc.Should().Be(BaseTime.AddHours(2));
        liveCust2!.OrderCount.Should().Be(1);
        liveCust2.LifetimeValue.Should().Be(new Money(99m, Currency.USD));
        liveCheckpoint.Should().Be(10);

        await ctx.Store.TruncateAsync(CancellationToken.None);
        await ClearCheckpointAsync(connStr);
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection)
            .ReplayAsync(0, CancellationToken.None);

        (await ctx.Store.GetAsync(ctx.Cust1, CancellationToken.None)).Should().Be(liveCust1);
        (await ctx.Store.GetAsync(ctx.Cust2, CancellationToken.None)).Should().Be(liveCust2);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(liveCheckpoint);
    }

    [Fact]
    public async Task Replay_run_twice_is_idempotent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);
        var replayer = new ProjectionReplayer(ctx.EventStore, ctx.Projection);

        await replayer.ReplayAsync(0, CancellationToken.None);
        var firstCust1 = await ctx.Store.GetAsync(ctx.Cust1, CancellationToken.None);
        var firstCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // A second full replay re-applies nothing: the skip-guard sees the
        // checkpoint already past every event, so the netting never
        // double-applies (which would corrupt the count and lifetime value).
        await replayer.ReplayAsync(0, CancellationToken.None);

        (await ctx.Store.GetAsync(ctx.Cust1, CancellationToken.None)).Should().Be(firstCust1);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(firstCheckpoint);
    }

    // Cust1 places order A (50, at +1h) and order B (30, at +2h); cust2 places
    // order C (99, at +1h); then order A is cancelled. Global positions land
    // 1-3 (A), 4-6 (B), 7-9 (C), 10 (A's cancel).
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var store = new PostgresCustomerSummaryStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create());
        var projection = new CustomerSummaryProjection(
            store, NullLogger<CustomerSummaryProjection>.Instance);

        var cust1 = Guid.NewGuid();
        var cust2 = Guid.NewGuid();
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var orderC = Guid.NewGuid();
        var streamA = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderA);
        var streamB = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderB);
        var streamC = StreamId.ForAggregate<Order>(WellKnownTenants.Default, orderC);

        await eventStore.AppendAsync(streamA, 0,
        [
            Env(streamA, 1, new OrderDrafted(orderA, cust1, BaseTime)),
            Env(streamA, 2, new OrderLineAdded(
                orderA, Guid.NewGuid(), "SKU-A", 1, new Money(50m, Currency.USD), BaseTime)),
            Env(streamA, 3, new OrderPlaced(
                orderA, cust1, new Money(50m, Currency.USD), BaseTime.AddHours(1))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamB, 0,
        [
            Env(streamB, 1, new OrderDrafted(orderB, cust1, BaseTime)),
            Env(streamB, 2, new OrderLineAdded(
                orderB, Guid.NewGuid(), "SKU-B", 1, new Money(30m, Currency.USD), BaseTime)),
            Env(streamB, 3, new OrderPlaced(
                orderB, cust1, new Money(30m, Currency.USD), BaseTime.AddHours(2))),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamC, 0,
        [
            Env(streamC, 1, new OrderDrafted(orderC, cust2, BaseTime)),
            Env(streamC, 2, new OrderLineAdded(
                orderC, Guid.NewGuid(), "SKU-C", 1, new Money(99m, Currency.USD), BaseTime)),
            Env(streamC, 3, new OrderPlaced(
                orderC, cust2, new Money(99m, Currency.USD), BaseTime.AddHours(1))),
        ], CancellationToken.None);

        // Cancel order A (global position 10), after both customers' placements.
        // The netting recovers cust1 and 50 from the lookup recorded at A's
        // placement, across the position gap.
        await eventStore.AppendAsync(streamA, 3,
        [
            Env(streamA, 4, new OrderCancelled(orderA, "out of stock", Guid.NewGuid(), BaseTime.AddHours(3))),
        ], CancellationToken.None);

        return new RebuildContext(eventStore, checkpointStore, store, projection, cust1, cust2);
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

    private static InProcessMessageDispatcher BuildDispatcher(CustomerSummaryProjection projection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<OrderPlaced>>(projection);
        services.AddSingleton<IEventHandler<OrderCancelled>>(projection);
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
            .Register<OrderPlaced>()
            .Register<OrderCancelled>();

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
        PostgresCustomerSummaryStore Store,
        CustomerSummaryProjection Projection,
        Guid Cust1,
        Guid Cust2);
}
