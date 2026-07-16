using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Outbox;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The rebuild narrative for the multi-handler inventory projection. Three
// inventories exercise every handler and direction: invA has two reservations
// and a partial release, invB has adjustments in both directions, invC has a
// single open reservation. Replay-from-zero reproduces the live-dispatched rows
// (including the partial-release netting and the open reservation that must
// persist), and replay-run-twice is idempotent.
public class InventoryDashboardRebuildTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "inventory-dashboard";
    private static readonly DateTime BaseTime = new(2026, 5, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public InventoryDashboardRebuildTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Replay_from_zero_reproduces_the_live_dispatched_read_model()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);

        var canonical = await ReadAllAsync(ctx.EventStore);
        var dispatcher = BuildDispatcher(ctx.Projection);
        foreach (var envelope in canonical)
        {
            await dispatcher.DispatchAsync(ToOutboxMessage(envelope), CancellationToken.None);
        }

        var liveA = await ctx.Store.GetBySkuAsync("SKU-A", CancellationToken.None);
        var liveB = await ctx.Store.GetBySkuAsync("SKU-B", CancellationToken.None);
        var liveC = await ctx.Store.GetBySkuAsync("SKU-C", CancellationToken.None);
        var liveCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // Non-vacuous: invA reserved two lines (10 + 5) and released one (10),
        // netting reserved to 5; invB adjusted +50 then -20; invC reserved 25.
        liveA!.OnHandQuantity.Should().Be(100);
        liveA.ReservedQuantity.Should().Be(5);
        liveB!.OnHandQuantity.Should().Be(30);
        liveB.ReservedQuantity.Should().Be(0);
        liveC!.OnHandQuantity.Should().Be(200);
        liveC.ReservedQuantity.Should().Be(25);
        liveCheckpoint.Should().Be(11);

        await ctx.Store.TruncateAsync(CancellationToken.None);
        await ClearCheckpointAsync(connStr);
        await new ProjectionReplayer(ctx.EventStore, ctx.Projection, new StubTenantAccessor { Current = WellKnownTenants.Default })
            .ReplayAsync(0, CancellationToken.None);

        (await ctx.Store.GetBySkuAsync("SKU-A", CancellationToken.None)).Should().Be(liveA);
        (await ctx.Store.GetBySkuAsync("SKU-B", CancellationToken.None)).Should().Be(liveB);
        (await ctx.Store.GetBySkuAsync("SKU-C", CancellationToken.None)).Should().Be(liveC);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(liveCheckpoint);

        // invC's reservation is still open: the lookup row must persist through
        // the rebuild, not be prematurely cleaned up.
        await using var read = await ctx.Store.BeginAsync(CancellationToken.None);
        (await read.GetReservationAsync(ctx.InvC, ctx.OrderC, ctx.LineC, CancellationToken.None))
            .Should().Be(new InventoryReservationRow(ctx.InvC, ctx.OrderC, ctx.LineC, 25, BaseTime));
    }

    [Fact]
    public async Task Replay_run_twice_is_idempotent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var ctx = await ArrangeAsync(dataSource);
        var replayer = new ProjectionReplayer(ctx.EventStore, ctx.Projection, new StubTenantAccessor { Current = WellKnownTenants.Default });

        await replayer.ReplayAsync(0, CancellationToken.None);
        var firstA = await ctx.Store.GetBySkuAsync("SKU-A", CancellationToken.None);
        var firstCheckpoint = await ctx.CheckpointStore.GetPositionAsync(
            ProjectionName, CancellationToken.None);

        // A second full replay re-applies nothing: the skip-guard sees the
        // checkpoint already past every event, so none of the four handlers
        // double-applies (which would corrupt on-hand or reserved).
        await replayer.ReplayAsync(0, CancellationToken.None);

        (await ctx.Store.GetBySkuAsync("SKU-A", CancellationToken.None)).Should().Be(firstA);
        (await ctx.CheckpointStore.GetPositionAsync(ProjectionName, CancellationToken.None))
            .Should().Be(firstCheckpoint);
    }

    // invA (SKU-A): created, +100 on-hand, reserve (o1,l1,10), reserve (o2,l2,5),
    // release (o1,l1). invB (SKU-B): created, +50, -20. invC (SKU-C): created,
    // +200, reserve (o3,l3,25). Positions 1-5 (A), 6-8 (B), 9-11 (C).
    private static async Task<RebuildContext> ArrangeAsync(NpgsqlDataSource dataSource)
    {
        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(dataSource);
        var checkpointStore = new PostgresCheckpointStore(readModelFactory);
        var store = new PostgresInventoryDashboardStore(
            readModelFactory, checkpointStore, TestNotificationPublisher.Create(), new StubTenantAccessor { Current = WellKnownTenants.Default });
        var projection = new InventoryDashboardProjection(
            store, NullLogger<InventoryDashboardProjection>.Instance);

        var invA = Guid.NewGuid();
        var invB = Guid.NewGuid();
        var invC = Guid.NewGuid();
        var o1 = Guid.NewGuid();
        var l1 = Guid.NewGuid();
        var o2 = Guid.NewGuid();
        var l2 = Guid.NewGuid();
        var orderC = Guid.NewGuid();
        var lineC = Guid.NewGuid();
        var streamA = StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, invA);
        var streamB = StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, invB);
        var streamC = StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, invC);

        await eventStore.AppendAsync(streamA, 0,
        [
            Env(streamA, 1, new InventoryCreated(invA, "SKU-A", BaseTime)),
            Env(streamA, 2, new InventoryAdjusted(invA, "SKU-A", 100, "restock", BaseTime)),
            Env(streamA, 3, new InventoryReserved(invA, o1, l1, "SKU-A", 10, BaseTime)),
            Env(streamA, 4, new InventoryReserved(invA, o2, l2, "SKU-A", 5, BaseTime)),
            Env(streamA, 5, new InventoryReleased(invA, o1, l1, "cancelled", BaseTime)),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamB, 0,
        [
            Env(streamB, 1, new InventoryCreated(invB, "SKU-B", BaseTime)),
            Env(streamB, 2, new InventoryAdjusted(invB, "SKU-B", 50, "restock", BaseTime)),
            Env(streamB, 3, new InventoryAdjusted(invB, "SKU-B", -20, "shrinkage", BaseTime)),
        ], CancellationToken.None);

        await eventStore.AppendAsync(streamC, 0,
        [
            Env(streamC, 1, new InventoryCreated(invC, "SKU-C", BaseTime)),
            Env(streamC, 2, new InventoryAdjusted(invC, "SKU-C", 200, "restock", BaseTime)),
            Env(streamC, 3, new InventoryReserved(invC, orderC, lineC, "SKU-C", 25, BaseTime)),
        ], CancellationToken.None);

        return new RebuildContext(eventStore, checkpointStore, store, projection, invC, orderC, lineC);
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
            Event: envelope.Payload,
            Metadata: envelope.Metadata,
            GlobalPosition: envelope.GlobalPosition,
            AttemptCount: 0);

    private static InProcessMessageDispatcher BuildDispatcher(InventoryDashboardProjection projection)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<InventoryCreated>>(projection);
        services.AddSingleton<IEventHandler<InventoryAdjusted>>(projection);
        services.AddSingleton<IEventHandler<InventoryReserved>>(projection);
        services.AddSingleton<IEventHandler<InventoryReleased>>(projection);
        services.AddSingleton<ICurrentTenantAccessor>(new StubTenantAccessor { Current = WellKnownTenants.Default });
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
            .Register<InventoryCreated>()
            .Register<InventoryAdjusted>()
            .Register<InventoryReserved>()
            .Register<InventoryReleased>();

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
        PostgresInventoryDashboardStore Store,
        InventoryDashboardProjection Projection,
        Guid InvC,
        Guid OrderC,
        Guid LineC);
}
