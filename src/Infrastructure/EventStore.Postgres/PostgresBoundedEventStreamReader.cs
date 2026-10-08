using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// The SQL limit bounds transferred history before hydration. The extra row only signals HasMore;
// it is never deserialized, so an unknown event beyond the requested window cannot break that window.
public sealed class PostgresBoundedEventStreamReader : IBoundedEventStreamReader
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly JsonSerializerOptions _options;
    private readonly EventUpcasterPipeline _pipeline;

    public PostgresBoundedEventStreamReader(
        INpgsqlConnectionFactory factory, JsonSerializerOptions options, EventUpcasterPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pipeline);
        _factory = factory;
        _options = options;
        _pipeline = pipeline;
    }

    public async Task<EventStreamWindow> ReadAsync(StreamId streamId, int maxEvents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEvents, 500);
        ct.ThrowIfCancellationRequested();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_version, event_id, event_type, event_version, payload, metadata,
                   occurred_utc, global_position
            FROM event_store.events
            WHERE stream_id = @stream
            ORDER BY stream_version
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, maxEvents + 1);
        var events = new List<EventEnvelope>(maxEvents);
        await using var rows = await command.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            if (events.Count == maxEvents) return new EventStreamWindow(events.AsReadOnly(), true);
            ct.ThrowIfCancellationRequested();
            try
            {
                var type = rows.GetString(2);
                var storedVersion = rows.GetInt16(3);
                var clrType = _pipeline.ResolveType(type, storedVersion);
                var stored = (IDomainEvent?)JsonSerializer.Deserialize(rows.GetString(4), clrType, _options)
                    ?? throw new JsonException("An event payload cannot be null.");
                var payload = _pipeline.Upcast(type, storedVersion, stored);
                events.Add(new EventEnvelope(streamId, rows.GetInt32(0), rows.GetGuid(1), type,
                    _pipeline.CurrentVersionFor(type), payload, EventMetadataReader.Read(rows.GetString(5), _options),
                    DateTime.SpecifyKind(rows.GetDateTime(6), DateTimeKind.Utc), rows.GetInt64(7)));
            }
            catch (Exception ex) when (ex is UnknownEventTypeException or UnknownEventSchemaVersionException
                or JsonException or InvalidCastException or ArgumentException or InvalidOperationException)
            {
                throw new EventStreamReadException(ex);
            }
        }
        return new EventStreamWindow(events.AsReadOnly(), false);
    }
}
