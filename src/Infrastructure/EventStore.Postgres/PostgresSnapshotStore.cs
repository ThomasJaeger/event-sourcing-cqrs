using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// The snapshot store on PostgreSQL (Chapter 12): upsert a memento into event_store.snapshots keyed by
// stream, and load it back by primary key. It sits with the other event_store-schema stores here
// rather than in a project of its own, the same placement PostgresIdempotencyStore has for the same
// reason: it consumes the same INpgsqlConnectionFactory and the table lives in the event_store schema.
public sealed class PostgresSnapshotStore : ISnapshotStore
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresSnapshotStore(INpgsqlConnectionFactory factory, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _jsonOptions = jsonOptions;
    }

    // One row per stream, upserted on capture: the newest snapshot replaces the older one, so a load is
    // always a single primary-key lookup. captured_utc is a wall-clock stamp for operational visibility;
    // nothing reads it back.
    public async Task SaveAsync<TSnapshot>(
        StreamId streamId,
        TSnapshot snapshot,
        int streamVersion,
        int snapshotSchemaVersion,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.snapshots " +
            "(stream_id, stream_version, snapshot_schema_version, payload, captured_utc) " +
            "VALUES (@stream_id, @stream_version, @snapshot_schema_version, @payload, @captured_utc) " +
            "ON CONFLICT (stream_id) DO UPDATE SET " +
            "stream_version = EXCLUDED.stream_version, " +
            "snapshot_schema_version = EXCLUDED.snapshot_schema_version, " +
            "payload = EXCLUDED.payload, " +
            "captured_utc = EXCLUDED.captured_utc";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId.Value);
        cmd.Parameters.AddWithValue("stream_version", NpgsqlDbType.Integer, streamVersion);
        cmd.Parameters.AddWithValue(
            "snapshot_schema_version", NpgsqlDbType.Smallint, (short)snapshotSchemaVersion);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        cmd.Parameters.AddWithValue("captured_utc", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The schema-version match is in the WHERE clause, not a check after reading, so a snapshot the
    // caller cannot consume never crosses the wire as a payload to deserialize. It reads as a miss,
    // indistinguishable from an absent stream, and the caller rebuilds the aggregate from events.
    public async Task<SnapshotEnvelope<TSnapshot>?> LoadAsync<TSnapshot>(
        StreamId streamId,
        int expectedSchemaVersion,
        CancellationToken ct)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT stream_version, payload FROM event_store.snapshots " +
            "WHERE stream_id = @stream_id AND snapshot_schema_version = @expected_schema_version";
        cmd.Parameters.AddWithValue("stream_id", NpgsqlDbType.Text, streamId.Value);
        cmd.Parameters.AddWithValue(
            "expected_schema_version", NpgsqlDbType.Smallint, (short)expectedSchemaVersion);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var streamVersion = reader.GetInt32(0);
        var snapshot = JsonSerializer.Deserialize<TSnapshot>(reader.GetString(1), _jsonOptions)!;
        return new SnapshotEnvelope<TSnapshot>(snapshot, streamVersion);
    }
}
