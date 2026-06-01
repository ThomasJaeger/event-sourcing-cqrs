using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// Integration coverage for the tenant on the metadata column: a new event and a
// new PM event carry a non-default tenant through the JSONB round-trip, and a
// pre-tenancy row (no tenant_id key, or an explicit null) reads back as the
// default tenant through the tolerant seam.
public class PostgresEventStore_Tenant_Tests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTime At = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    public PostgresEventStore_Tenant_Tests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_new_event_round_trips_a_non_default_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamId = NewStreamId();
        await store.AppendAsync(
            streamId, 0,
            [BuildEnvelope(streamId, 1, new TestPayload(Guid.NewGuid(), 5m), tenant: OtherTenant)],
            CancellationToken.None);

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].Metadata.Tenant.Should().Be(OtherTenant);
    }

    [Fact]
    public async Task A_legacy_row_with_no_tenant_id_key_reads_as_the_default_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamId = NewStreamId();
        await InsertLegacyEventAsync(connStr, streamId, tenantIdJson: null);

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].Metadata.Tenant.Should().Be(WellKnownTenants.Default);
    }

    [Fact]
    public async Task A_legacy_row_with_a_null_tenant_id_reads_as_the_default_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), CreateRegistry(), CreatePmRegistry(), CreateJsonOptions());
        var streamId = NewStreamId();
        await InsertLegacyEventAsync(connStr, streamId, tenantIdJson: "null");

        var read = await store.ReadStreamAsync(streamId, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].Metadata.Tenant.Should().Be(WellKnownTenants.Default);
    }

    [Fact]
    public async Task A_pm_event_round_trips_a_non_default_tenant()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var store = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource),
            CreateRegistry(),
            new ProcessManagerEventTypeRegistry().Register<TenantPmEvent>(),
            CreateJsonOptions());
        var stream = StreamId.ForProcessManager(StreamPrefixes.OrderFulfillmentPm, OtherTenant, Guid.NewGuid());
        var eventId = Guid.NewGuid();
        var envelope = new ProcessManagerEventEnvelope(
            StreamId: stream,
            StreamVersion: 1,
            EventId: eventId,
            EventType: nameof(TenantPmEvent),
            EventVersion: 1,
            Payload: new TenantPmEvent(1),
            Metadata: new EventMetadata(
                EventId: eventId,
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                SchemaVersion: 1,
                OccurredUtc: At,
                Tenant: OtherTenant),
            OccurredUtc: At,
            GlobalPosition: 0);
        await store.AppendProcessManagerEventsAsync(stream, 0, [envelope], CancellationToken.None);

        var read = await store.ReadProcessManagerStreamAsync(stream, 0, CancellationToken.None);

        read.Should().HaveCount(1);
        read[0].Metadata.Tenant.Should().Be(OtherTenant);
    }

    // Inserts a pre-tenancy events row whose metadata blob carries the usual keys
    // but no tenant. tenantIdJson null omits the key entirely; "null" writes an
    // explicit JSON null. Both are the legacy shapes the read seam tolerates.
    private static async Task InsertLegacyEventAsync(string connStr, StreamId streamId, string? tenantIdJson)
    {
        var tenantEntry = tenantIdJson is null ? "" : $",\"tenant_id\":{tenantIdJson}";
        var metadataJson =
            $"{{\"event_id\":\"{Guid.NewGuid()}\",\"correlation_id\":\"{Guid.NewGuid()}\"," +
            $"\"causation_id\":\"{Guid.NewGuid()}\",\"actor_id\":\"{Guid.Empty}\"," +
            $"\"source\":\"legacy\",\"schema_version\":1,\"occurred_utc\":\"2026-01-01T00:00:00Z\"{tenantEntry}}}";
        var payloadJson = JsonSerializer.Serialize(new TestPayload(Guid.NewGuid(), 1m), CreateJsonOptions());

        await using var connection = new NpgsqlConnection(connStr);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.events " +
            "(stream_id, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc) " +
            "VALUES (@stream_id, 1, @event_id, @event_type, 1, @payload, @metadata, now())";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId.Value);
        cmd.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, nameof(TestPayload));
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadataJson);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record TenantPmEvent(int Step) : IProcessManagerEvent;
}
