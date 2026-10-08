using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Chapter 17: inspect recorded documents without resolving payload types or running upcasters.
// The row cap bounds the response; selective metadata filters can still examine many stored rows.
public sealed class PostgresAuditEventReader : IAuditEventReader
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresAuditEventReader(INpgsqlConnectionFactory factory, JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _jsonOptions = jsonOptions;
    }

    public async Task<AuditEventPage> ReadPageAsync(AuditEventFilter filter, long? upperPosition,
        long? beforePosition, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (pageSize is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Audit pages contain between 1 and 50 events.");
        if (upperPosition < 0)
            throw new ArgumentOutOfRangeException(nameof(upperPosition));
        if (beforePosition is not null && (upperPosition is null || beforePosition <= 0 || beforePosition > upperPosition))
            throw new ArgumentException("A page cursor requires its captured upper position and must fall within it.", nameof(beforePosition));
        if (filter.StreamId is not null) _ = StreamId.Parse(filter.StreamId);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        // One statement captures the head and reads the page under the same PostgreSQL statement
        // snapshot. A left join keeps that head available even when the selected page is empty.
        command.CommandText = """
            WITH boundary AS MATERIALIZED (
                SELECT COALESCE(@upper, (SELECT MAX(global_position) FROM event_store.events), 0) AS upper_position
            ), page_rows AS MATERIALIZED (
                SELECT e.*
                FROM event_store.events AS e CROSS JOIN boundary AS b
                WHERE e.global_position <= b.upper_position
                  AND (@before IS NULL OR e.global_position < @before)
                  AND (@stream IS NULL OR e.stream_id = @stream)
                  AND (@correlation IS NULL OR e.correlation_id = @correlation)
                  AND (@tenant IS NULL OR e.tenant_id = @tenant)
                  AND (@actor IS NULL OR COALESCE((e.metadata->>'actor_id')::uuid,
                       '00000000-0000-0000-0000-000000000000'::uuid) = @actor)
                ORDER BY e.global_position DESC
                LIMIT @limit
            )
            SELECT b.upper_position, p.global_position, p.stream_id, p.stream_version,
                   p.event_id, p.event_type, p.event_version, p.occurred_utc, p.payload, p.metadata
            FROM boundary AS b LEFT JOIN page_rows AS p ON TRUE
            ORDER BY p.global_position DESC
            """;
        command.Parameters.AddWithValue("upper", NpgsqlDbType.Bigint, (object?)upperPosition ?? DBNull.Value);
        command.Parameters.AddWithValue("before", NpgsqlDbType.Bigint, (object?)beforePosition ?? DBNull.Value);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, (object?)filter.StreamId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, (object?)filter.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, (object?)filter.Tenant?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)filter.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);

        var rows = new List<AuditEventRow>();
        long boundary = 0;
        long? nextBeforePosition = null;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            boundary = reader.GetInt64(0);
            if (reader.IsDBNull(1)) break;
            if (rows.Count == pageSize)
            {
                nextBeforePosition = rows[^1].GlobalPosition;
                break;
            }
            var metadataJson = reader.GetString(9);
            rows.Add(new AuditEventRow(reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetGuid(4), reader.GetString(5), reader.GetInt16(6),
                DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                EventMetadataReader.Read(metadataJson, _jsonOptions), reader.GetString(8), metadataJson));
        }
        return new(rows, boundary, nextBeforePosition);
    }
}
