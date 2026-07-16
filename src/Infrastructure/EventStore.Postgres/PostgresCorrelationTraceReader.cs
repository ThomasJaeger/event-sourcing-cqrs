using System.Data.Common;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Hand-rolled PostgreSQL implementation of ICorrelationTraceReader for the AdminConsole
// Correlation-ID Tracer (Chapter 17). One query on the indexed correlation_id column
// answers a whole trace: aggregate rows and process-manager rows share the events table
// (ADR 0013), so no join and no second query are needed to see both.
//
// The read resolves no CLR payload type. It holds neither event-type registry, and the
// payload leaves here as the JSON text the store holds. That is what lets one query serve
// both event families: the two resolve through separate registries, and any hydrating read
// would have to pick one of them and throw on the other's rows.
//
// Metadata is the exception, and deliberately so. It deserializes through EventMetadataReader,
// the same tolerant seam every other read in this adapter uses, and a failure there surfaces
// rather than degrading the row. Metadata is the platform's own fixed shape, so a row that
// cannot produce it is a defect the operator needs to see, not a row to render half of.
//
// Self-contained per ADR 0004, and separate from the aggregate-lifecycle IEventStore for the
// reason IEventStoreHeadPosition is: reading the global log by correlation is not a stream
// operation.
public sealed class PostgresCorrelationTraceReader : ICorrelationTraceReader
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresCorrelationTraceReader(
        INpgsqlConnectionFactory factory,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        _factory = factory;
        _jsonOptions = jsonOptions;
    }

    public async Task<CorrelationTraceResult> ReadTraceAsync(
        Guid correlationId,
        int maxRows,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position, stream_id, stream_version, event_id, event_type, " +
            "event_version, payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE correlation_id = @correlation_id " +
            "ORDER BY global_position " +
            "LIMIT @limit";
        AddUuid(cmd, "correlation_id", correlationId);

        // Fetch one row past the cap. Its presence is the truncation signal, so the caller
        // learns the trace was cut without a second count query over the same index. The
        // parameter is a bigint so maxRows at int.MaxValue cannot overflow the increment.
        AddBigInt(cmd, "limit", (long)maxRows + 1);

        var rows = new List<CorrelationTraceRow>();
        var truncated = false;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // The row past the cap is counted and dropped. It never materializes and never
            // reaches the caller; only the flag it sets does.
            if (rows.Count == maxRows)
            {
                truncated = true;
                break;
            }

            rows.Add(ReadRow(reader));
        }

        return new CorrelationTraceResult(rows, truncated);
    }

    private CorrelationTraceRow ReadRow(DbDataReader reader)
        => new(
            StreamId: reader.GetString(1),
            StreamVersion: reader.GetInt32(2),
            GlobalPosition: reader.GetInt64(0),
            EventId: reader.GetGuid(3),
            EventType: reader.GetString(4),
            EventVersion: reader.GetInt16(5),
            PayloadJson: reader.GetString(6),
            Metadata: EventMetadataReader.Read(reader.GetString(7), _jsonOptions),
            OccurredUtc: DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc));

    private static void AddUuid(NpgsqlCommand cmd, string name, Guid value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);

    private static void AddBigInt(NpgsqlCommand cmd, string name, long value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, value);
}
