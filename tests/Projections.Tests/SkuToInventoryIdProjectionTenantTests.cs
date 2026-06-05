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
    private static readonly DateTime At = new(2026, 5, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public SkuToInventoryIdProjectionTenantTests(PostgresFixture fixture) => _fixture = fixture;

    // The (tenant_id, sku) key isolation case lives once in CrossTenantProjectionCases, run both here and
    // from the registry-driven meta-test; the harness this case uses stays in this file.
    [Fact]
    public Task SkuToInventoryId_records_one_row_per_tenant_for_the_same_sku_once_the_key_is_tenant_scoped()
        => CrossTenantProjectionCases.For(_fixture)[typeof(SkuToInventoryIdProjection)]();

    internal static EventEnvelope Env(StreamId streamId, int version, IDomainEvent payload, TenantId tenant)
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

    internal static async Task<List<(Guid TenantId, Guid InventoryId)>> ReadMappingRowsAsync(
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

    internal static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new TenantIdJsonConverter() },
        };
}
