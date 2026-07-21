using System.Data;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// The snapshot store on SQL Server (Chapter 12), the twin of PostgresSnapshotStore: upsert a memento
// into event_store.snapshots keyed by stream, and load it back by primary key. It sits with the other
// event_store-schema stores here per ADR 0004 (each adapter is self-contained): it consumes the same
// ISqlServerConnectionFactory and JsonSerializerOptions the event store does, and the table lives in
// the event_store schema.
public sealed class SqlServerSnapshotStore : ISnapshotStore
{
    private readonly ISqlServerConnectionFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqlServerSnapshotStore(ISqlServerConnectionFactory factory, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _jsonOptions = jsonOptions;
    }

    // One row per stream, upserted on capture. T-SQL has no ON CONFLICT DO UPDATE, so the upsert is an
    // UPDATE under (UPDLOCK, HOLDLOCK) then a conditional INSERT inside one transaction: HOLDLOCK takes a
    // range lock on the key even when no row exists, so a second capture of the same stream blocks until
    // the first commits and then updates the row it now sees. Same concurrency posture as
    // SqlServerIdempotencyStore, and the reason a bare MERGE is not used here (it is not concurrency-safe
    // without the same hint). captured_utc is a wall-clock stamp for operational visibility; nothing
    // reads it back.
    public async Task SaveAsync<TSnapshot>(
        StreamId streamId,
        TSnapshot snapshot,
        int streamVersion,
        int snapshotSchemaVersion,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "SET XACT_ABORT ON; " +
            "BEGIN TRANSACTION; " +
            "UPDATE event_store.snapshots WITH (UPDLOCK, HOLDLOCK) " +
            "SET stream_version = @stream_version, " +
            "snapshot_schema_version = @snapshot_schema_version, " +
            "payload = @payload, " +
            "captured_utc = @captured_utc " +
            "WHERE stream_id = @stream_id; " +
            "IF @@ROWCOUNT = 0 " +
            "INSERT INTO event_store.snapshots " +
            "(stream_id, stream_version, snapshot_schema_version, payload, captured_utc) " +
            "VALUES (@stream_id, @stream_version, @snapshot_schema_version, @payload, @captured_utc); " +
            "COMMIT TRANSACTION;",
            connection);
        AddIdentifier(cmd, "stream_id", streamId.Value);
        cmd.Parameters.Add("@stream_version", SqlDbType.Int).Value = streamVersion;
        cmd.Parameters.Add("@snapshot_schema_version", SqlDbType.SmallInt).Value = (short)snapshotSchemaVersion;
        AddJson(cmd, "payload", payload);
        cmd.Parameters.Add("@captured_utc", SqlDbType.DateTimeOffset).Value = DateTimeOffset.UtcNow;
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
        await using var cmd = new SqlCommand(
            "SELECT stream_version, payload FROM event_store.snapshots " +
            "WHERE stream_id = @stream_id AND snapshot_schema_version = @expected_schema_version",
            connection);
        AddIdentifier(cmd, "stream_id", streamId.Value);
        cmd.Parameters.Add("@expected_schema_version", SqlDbType.SmallInt).Value = (short)expectedSchemaVersion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var streamVersion = reader.GetInt32(0);
        var snapshot = JsonSerializer.Deserialize<TSnapshot>(reader.GetString(1), _jsonOptions)!;
        return new SnapshotEnvelope<TSnapshot>(snapshot, streamVersion);
    }

    // VarChar to match the stream_id column and keep the primary-key seek, the same reason the event
    // store binds its identifier columns this way.
    private static void AddIdentifier(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.VarChar, 200).Value = value;

    // NVarChar against the VARCHAR(MAX) UTF-8 payload column: binding it as VarChar would encode to the
    // connection codepage before the value leaves the process and destroy non-ASCII silently. NVarChar
    // sends Unicode and lets the server convert into the UTF-8 column losslessly.
    private static void AddJson(SqlCommand cmd, string name, string json)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = json;
}
