using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.Infrastructure;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// Commit 2 RED: the SKU-to-InventoryId lookup must key on (tenant_id, sku) so two
// tenants can each map the same sku. Replay drives the projection (the live and
// replay paths Commit 1 tenanted) over two InventoryCreated events for one shared
// sku, stamped with different tenants in their metadata.
public class SkuToInventoryIdProjectionTenantTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA =
        TenantId.From(Guid.Parse("a0000000-0000-0000-0000-0000000000a1"));
    private static readonly TenantId TenantB =
        TenantId.From(Guid.Parse("b0000000-0000-0000-0000-0000000000b2"));
    private static readonly DateTime At = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public SkuToInventoryIdProjectionTenantTests(PostgresFixture fixture) => _fixture = fixture;

    // RED: the current upsert binds no tenant_id (it relies on the 0017 column
    // default) and its ON CONFLICT (sku) DO NOTHING swallows the second event, so
    // only one defaulted row exists and the two-rows assertion fails.
    [Fact]
    public async Task SkuToInventoryId_records_one_row_per_tenant_for_the_same_sku_once_the_key_is_tenant_scoped()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        const string sku = "SKU-SHARED";
        var inventoryA = Guid.NewGuid();
        var inventoryB = Guid.NewGuid();

        var eventStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(ds),
            new EventTypeRegistry().Register<InventoryCreated>(),
            new ProcessManagerEventTypeRegistry(),
            JsonOptions());
        var readModelFactory = new NpgsqlReadModelConnectionFactory(ds);
        var tenantAccessor = new StubTenantAccessor();
        var store = new PostgresSkuToInventoryIdStore(
            readModelFactory, new PostgresCheckpointStore(readModelFactory), TestNotificationPublisher.Create(),
            tenantAccessor);
        var projection = new SkuToInventoryIdProjection(store);

        var streamA = StreamId.ForAggregate<Inventory>(TenantA, inventoryA);
        var streamB = StreamId.ForAggregate<Inventory>(TenantB, inventoryB);
        await eventStore.AppendAsync(streamA, 0,
            [Env(streamA, 1, new InventoryCreated(inventoryA, sku, At), TenantA)], CancellationToken.None);
        await eventStore.AppendAsync(streamB, 0,
            [Env(streamB, 1, new InventoryCreated(inventoryB, sku, At), TenantB)], CancellationToken.None);

        // The replayer set-point supplies each event's metadata tenant to the accessor
        // around the invoke (Commit 1); the projection write reads it the same way the
        // four primary projections do.
        await new ProjectionReplayer(eventStore, projection, tenantAccessor)
            .ReplayAsync(0, CancellationToken.None);

        var rows = await ReadMappingRowsAsync(connStr, sku);
        rows.Should().BeEquivalentTo(new[]
        {
            (TenantA.Value, inventoryA),
            (TenantB.Value, inventoryB),
        }, "each tenant's InventoryCreated for the same sku must record its own (tenant_id, inventory_id) mapping");
    }

    private static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload, TenantId tenant)
    {
        var eventId = Guid.NewGuid();
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: At,
            Tenant: tenant);
        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: version,
            EventId: eventId,
            EventType: payload.GetType().Name,
            EventVersion: 1,
            Payload: payload,
            Metadata: metadata,
            OccurredUtc: At,
            GlobalPosition: 0);
    }

    private static async Task<List<(Guid TenantId, Guid InventoryId)>> ReadMappingRowsAsync(
        string connStr, string sku)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT tenant_id, inventory_id FROM read_models.sku_to_inventory_id " +
            "WHERE sku = @sku ORDER BY tenant_id";
        cmd.Parameters.AddWithValue("sku", NpgsqlDbType.Text, sku);
        var rows = new List<(Guid, Guid)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }
        return rows;
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        };
}
