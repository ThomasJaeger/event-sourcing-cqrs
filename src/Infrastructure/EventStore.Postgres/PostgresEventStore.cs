using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Hand-rolled PostgreSQL implementation of IEventStore. Each AppendAsync
// call writes the event row and its matching outbox row inside a single
// NpgsqlTransaction. The outbox table is an implementation detail of
// this adapter; the public IEventStore surface does not mention it.
//
// Self-contained per ADR 0004. The SQL Server adapter that ships in a
// later Phase 2 session duplicates this structural shape in its own
// project with its own engine-specific particulars (error 2627 for
// unique violations, NVARCHAR(MAX) for JSON, filtered indexes).
public sealed class PostgresEventStore : IEventStore
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly EventTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresEventStore(
        INpgsqlConnectionFactory factory,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _registry = registry;
        _jsonOptions = jsonOptions;
    }

    public async Task AppendAsync(
        Guid streamId,
        int expectedVersion,
        IReadOnlyList<EventEnvelope> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            foreach (var envelope in events)
            {
                var eventTypeName = _registry.NameFor(envelope.Payload.GetType());
                var payloadJson = JsonSerializer.Serialize(
                    envelope.Payload, envelope.Payload.GetType(), _jsonOptions);
                var metadataJson = JsonSerializer.Serialize(envelope.Metadata, _jsonOptions);

                await using (var insertEvent = connection.CreateCommand())
                {
                    insertEvent.Transaction = transaction;
                    insertEvent.CommandText =
                        "INSERT INTO event_store.events " +
                        "(stream_id, stream_version, event_id, event_type, event_version, " +
                        "payload, metadata, occurred_utc) " +
                        "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                        "@event_version, @payload, @metadata, @occurred_utc)";
                    AddUuid(insertEvent, "stream_id", envelope.StreamId);
                    AddInteger(insertEvent, "stream_version", envelope.StreamVersion);
                    AddUuid(insertEvent, "event_id", envelope.EventId);
                    AddText(insertEvent, "event_type", eventTypeName);
                    AddSmallInt(insertEvent, "event_version", (short)envelope.EventVersion);
                    AddJsonb(insertEvent, "payload", payloadJson);
                    AddJsonb(insertEvent, "metadata", metadataJson);
                    AddTimestampTz(insertEvent, "occurred_utc", envelope.OccurredUtc);
                    await insertEvent.ExecuteNonQueryAsync(ct);
                }

                // sent_utc, last_error, next_attempt_at default to NULL; attempt_count
                // defaults to 0. Only the NOT NULL columns appear in this INSERT.
                await using (var insertOutbox = connection.CreateCommand())
                {
                    insertOutbox.Transaction = transaction;
                    insertOutbox.CommandText =
                        "INSERT INTO event_store.outbox " +
                        "(event_id, event_type, payload, metadata, occurred_utc) " +
                        "VALUES (@event_id, @event_type, @payload, @metadata, @occurred_utc)";
                    AddUuid(insertOutbox, "event_id", envelope.EventId);
                    AddText(insertOutbox, "event_type", eventTypeName);
                    AddJsonb(insertOutbox, "payload", payloadJson);
                    AddJsonb(insertOutbox, "metadata", metadataJson);
                    AddTimestampTz(insertOutbox, "occurred_utc", envelope.OccurredUtc);
                    await insertOutbox.ExecuteNonQueryAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "uq_events_stream_version")
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }
    }

    public async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        Guid streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE stream_id = @stream_id AND stream_version > @from_version " +
            "ORDER BY stream_version";
        AddUuid(cmd, "stream_id", streamId);
        AddInteger(cmd, "from_version", fromVersion);

        var envelopes = new List<EventEnvelope>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var globalPosition = reader.GetInt64(0);
            var streamVersion = reader.GetInt32(1);
            var eventId = reader.GetGuid(2);
            var eventType = reader.GetString(3);
            var eventVersion = reader.GetInt16(4);
            var payloadJson = reader.GetString(5);
            var metadataJson = reader.GetString(6);
            var occurredUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc);

            Type clrType;
            try
            {
                clrType = _registry.TypeFor(eventType);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IDomainEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            var metadata = JsonSerializer.Deserialize<EventMetadata>(
                metadataJson, _jsonOptions)!;

            envelopes.Add(new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: eventVersion,
                Payload: payload,
                Metadata: metadata,
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition));
        }

        return envelopes;
    }

    // Streams the whole events table in global_position order, yielding rows
    // as the reader produces them. SequentialAccess keeps the reader from
    // buffering whole rows; the single connection is held open for the
    // enumeration's lifetime. v1 read loads tolerate this; very large rebuilds
    // may need keyset-paginated batching, deferred until that is a real concern.
    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position, stream_id, stream_version, event_id, event_type, " +
            "event_version, payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE global_position > @from_position " +
            "ORDER BY global_position";
        AddBigInt(cmd, "from_position", fromPosition);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            // ReadAsync only observes the token when it does real async I/O, so a
            // buffered result set would not see a cancellation between rows. Check
            // explicitly each iteration so enumeration stops deterministically.
            ct.ThrowIfCancellationRequested();

            // SequentialAccess requires reading columns in ascending ordinal order.
            var globalPosition = reader.GetInt64(0);
            var streamId = reader.GetGuid(1);
            var streamVersion = reader.GetInt32(2);
            var eventId = reader.GetGuid(3);
            var eventType = reader.GetString(4);
            var eventVersion = reader.GetInt16(5);
            var payloadJson = reader.GetString(6);
            var metadataJson = reader.GetString(7);
            var occurredUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc);

            Type clrType;
            try
            {
                clrType = _registry.TypeFor(eventType);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IDomainEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            var metadata = JsonSerializer.Deserialize<EventMetadata>(
                metadataJson, _jsonOptions)!;

            yield return new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: eventVersion,
                Payload: payload,
                Metadata: metadata,
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition);
        }
    }

    private static void AddUuid(NpgsqlCommand cmd, string name, Guid value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);

    private static void AddText(NpgsqlCommand cmd, string name, string value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

    private static void AddInteger(NpgsqlCommand cmd, string name, int value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Integer, value);

    private static void AddBigInt(NpgsqlCommand cmd, string name, long value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, value);

    private static void AddSmallInt(NpgsqlCommand cmd, string name, short value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Smallint, value);

    private static void AddJsonb(NpgsqlCommand cmd, string name, string json)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, json);

    private static void AddTimestampTz(NpgsqlCommand cmd, string name, DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"Expected DateTimeKind.Utc on TIMESTAMPTZ parameter '{name}', got {utc.Kind}.",
                nameof(utc));
        }
        cmd.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, utc);
    }
}
