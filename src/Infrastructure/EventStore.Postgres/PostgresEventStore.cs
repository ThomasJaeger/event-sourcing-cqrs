using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Hand-rolled PostgreSQL implementation of IEventStore. Each AppendAsync
// call writes the event row and its matching outbox row inside a single
// NpgsqlTransaction. The outbox table is an implementation detail of
// this adapter; the public IEventStore surface does not mention it.
//
// Self-contained per ADR 0004. The SQL Server adapter duplicates this
// structural shape in its own project with its own engine-specific
// particulars (errors 2627 and 2601 for unique violations, VARCHAR(MAX)
// under a UTF-8 collation for JSON, filtered indexes).
public sealed class PostgresEventStore : IEventStore
{
    // pg_advisory_xact_lock(bigint) key serializing global_position assignment against
    // commit. IDENTITY hands out a position mid-transaction, so without this two appends
    // can commit out of position order and a reader tailing the log skips the one that
    // commits late. Both append transactions take this lock before drawing a position, and
    // the commit-visibility test's held writer takes the same key. The eight bytes spell
    // ASCII "ESRCQ_AP" (event-sourcing reference implementation, append); distinct from
    // MigrationRunner.MigrationAdvisoryLockKey, which shares the instance's lock space.
    public const long AppendAdvisoryLockKey = 0x4553_5243_515F_4150L;

    private readonly INpgsqlConnectionFactory _factory;
    private readonly EventTypeRegistry _registry;
    private readonly ProcessManagerEventTypeRegistry _pmRegistry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly EventUpcasterPipeline _pipeline;

    public PostgresEventStore(
        INpgsqlConnectionFactory factory,
        EventTypeRegistry registry,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions,
        EventUpcasterPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pmRegistry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(pipeline);
        _factory = factory;
        _registry = registry;
        _pmRegistry = pmRegistry;
        _jsonOptions = jsonOptions;
        _pipeline = pipeline;
    }

    public async Task AppendAsync(
        StreamId streamId,
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
            await SerializePositionAssignmentAsync(connection, transaction, ct);

            foreach (var envelope in events)
            {
                var eventTypeName = _registry.NameFor(envelope.Payload.GetType());
                var payloadJson = JsonSerializer.Serialize(
                    envelope.Payload, envelope.Payload.GetType(), _jsonOptions);
                var metadataJson = JsonSerializer.Serialize(envelope.Metadata, _jsonOptions);

                // RETURNING hands back the IDENTITY-assigned global_position so
                // the matching outbox row can carry it without a later read-back.
                long globalPosition;
                await using (var insertEvent = connection.CreateCommand())
                {
                    insertEvent.Transaction = transaction;
                    insertEvent.CommandText =
                        "INSERT INTO event_store.events " +
                        "(stream_id, stream_version, event_id, event_type, event_version, " +
                        "payload, metadata, occurred_utc) " +
                        "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                        "@event_version, @payload, @metadata, @occurred_utc) " +
                        "RETURNING global_position";
                    AddText(insertEvent, "stream_id", envelope.StreamId.Value);
                    AddInteger(insertEvent, "stream_version", envelope.StreamVersion);
                    AddUuid(insertEvent, "event_id", envelope.EventId);
                    AddText(insertEvent, "event_type", eventTypeName);
                    AddSmallInt(insertEvent, "event_version", (short)envelope.EventVersion);
                    AddJsonb(insertEvent, "payload", payloadJson);
                    AddJsonb(insertEvent, "metadata", metadataJson);
                    AddTimestampTz(insertEvent, "occurred_utc", envelope.OccurredUtc);
                    globalPosition = (long)(await insertEvent.ExecuteScalarAsync(ct))!;
                }

                // sent_utc, last_error, next_attempt_at default to NULL; attempt_count
                // defaults to 0. Only the NOT NULL columns appear in this INSERT.
                // global_position is threaded from the events RETURNING above so the
                // outbox row is self-describing and the OutboxProcessor never joins back.
                await using (var insertOutbox = connection.CreateCommand())
                {
                    insertOutbox.Transaction = transaction;
                    insertOutbox.CommandText =
                        "INSERT INTO event_store.outbox " +
                        "(event_id, event_type, event_version, payload, metadata, occurred_utc, " +
                        "global_position) " +
                        "VALUES (@event_id, @event_type, @event_version, @payload, @metadata, " +
                        "@occurred_utc, @global_position)";
                    AddUuid(insertOutbox, "event_id", envelope.EventId);
                    AddText(insertOutbox, "event_type", eventTypeName);
                    AddSmallInt(insertOutbox, "event_version", (short)envelope.EventVersion);
                    AddJsonb(insertOutbox, "payload", payloadJson);
                    AddJsonb(insertOutbox, "metadata", metadataJson);
                    AddTimestampTz(insertOutbox, "occurred_utc", envelope.OccurredUtc);
                    AddBigInt(insertOutbox, "global_position", globalPosition);
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
        StreamId streamId,
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
        AddText(cmd, "stream_id", streamId.Value);
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
                clrType = _pipeline.ResolveType(eventType, eventVersion);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IDomainEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            payload = _pipeline.Upcast(eventType, eventVersion, payload);
            var metadata = EventMetadataReader.Read(metadataJson, _jsonOptions);

            envelopes.Add(new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: _pipeline.CurrentVersionFor(eventType),
                Payload: payload,
                Metadata: metadata,
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition));
        }

        return envelopes;
    }

    // Writes PM events to the same events table as AppendAsync, honoring the
    // same (stream_id, stream_version) constraint, but writes no outbox row:
    // PM events are internal coordination state, not published (ADR 0013).
    public async Task AppendProcessManagerEventsAsync(
        StreamId streamId,
        int expectedVersion,
        IReadOnlyList<ProcessManagerEventEnvelope> events,
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
            await SerializePositionAssignmentAsync(connection, transaction, ct);

            foreach (var envelope in events)
            {
                var eventTypeName = _pmRegistry.NameFor(envelope.Payload.GetType());
                var payloadJson = JsonSerializer.Serialize(
                    envelope.Payload, envelope.Payload.GetType(), _jsonOptions);
                var metadataJson = JsonSerializer.Serialize(envelope.Metadata, _jsonOptions);

                await using var insertEvent = connection.CreateCommand();
                insertEvent.Transaction = transaction;
                insertEvent.CommandText =
                    "INSERT INTO event_store.events " +
                    "(stream_id, stream_version, event_id, event_type, event_version, " +
                    "payload, metadata, occurred_utc) " +
                    "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                    "@event_version, @payload, @metadata, @occurred_utc)";
                AddText(insertEvent, "stream_id", envelope.StreamId.Value);
                AddInteger(insertEvent, "stream_version", envelope.StreamVersion);
                AddUuid(insertEvent, "event_id", envelope.EventId);
                AddText(insertEvent, "event_type", eventTypeName);
                AddSmallInt(insertEvent, "event_version", (short)envelope.EventVersion);
                AddJsonb(insertEvent, "payload", payloadJson);
                AddJsonb(insertEvent, "metadata", metadataJson);
                AddTimestampTz(insertEvent, "occurred_utc", envelope.OccurredUtc);
                await insertEvent.ExecuteNonQueryAsync(ct);
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

    // Reads a PM stream for rehydration. Requires a pm- prefixed StreamId
    // (fail-loud per ADR 0011/0013) and resolves payload types through the PM
    // registry, never the aggregate registry.
    public async Task<IReadOnlyList<ProcessManagerEventEnvelope>> ReadProcessManagerStreamAsync(
        StreamId streamId,
        int fromVersion = 0,
        CancellationToken ct = default)
    {
        if (!streamId.Value.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ReadProcessManagerStreamAsync requires a process-manager stream id " +
                $"(pm- prefix); got '{streamId}'.",
                nameof(streamId));
        }

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE stream_id = @stream_id AND stream_version > @from_version " +
            "ORDER BY stream_version";
        AddText(cmd, "stream_id", streamId.Value);
        AddInteger(cmd, "from_version", fromVersion);

        var envelopes = new List<ProcessManagerEventEnvelope>();
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
                clrType = _pmRegistry.TypeFor(eventType);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IProcessManagerEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            var metadata = EventMetadataReader.Read(metadataJson, _jsonOptions);

            envelopes.Add(new ProcessManagerEventEnvelope(
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

    // Streams the events table excluding PM streams, in global_position order,
    // yielding rows as the reader produces them. SequentialAccess keeps the reader from
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
            "AND stream_id NOT LIKE 'pm-%' " +
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
            var streamId = StreamId.Parse(reader.GetString(1));
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
                clrType = _pipeline.ResolveType(eventType, eventVersion);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IDomainEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            payload = _pipeline.Upcast(eventType, eventVersion, payload);
            var metadata = EventMetadataReader.Read(metadataJson, _jsonOptions);

            yield return new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: _pipeline.CurrentVersionFor(eventType),
                Payload: payload,
                Metadata: metadata,
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition);
        }
    }

    // Pattern from Chapter 13 (per-tenant rebuild): the tenant-scoped, ceilinged twin of
    // ReadAllAsync. Same feed and row mapping; the predicate adds the tenant and an
    // inclusive upper bound, so a rebuild replays exactly one tenant's events up to the
    // captured checkpoint. The tenant_id column is the STORED generated column (0016).
    public async IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant, long fromPosition, long toPositionInclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT global_position, stream_id, stream_version, event_id, event_type, " +
            "event_version, payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE global_position > @from_position " +
            "AND global_position <= @to_position " +
            "AND tenant_id = @tenant " +
            "AND stream_id NOT LIKE 'pm-%' " +
            "ORDER BY global_position";
        AddBigInt(cmd, "from_position", fromPosition);
        AddBigInt(cmd, "to_position", toPositionInclusive);
        AddUuid(cmd, "tenant", tenant.Value);

        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var globalPosition = reader.GetInt64(0);
            var streamId = StreamId.Parse(reader.GetString(1));
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
                clrType = _pipeline.ResolveType(eventType, eventVersion);
            }
            catch (UnknownEventTypeException ex)
            {
                throw new UnknownEventTypeException(eventType, streamId, ex);
            }

            var payload = (IDomainEvent)JsonSerializer.Deserialize(
                payloadJson, clrType, _jsonOptions)!;
            payload = _pipeline.Upcast(eventType, eventVersion, payload);
            var metadata = EventMetadataReader.Read(metadataJson, _jsonOptions);

            yield return new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: _pipeline.CurrentVersionFor(eventType),
                Payload: payload,
                Metadata: metadata,
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition);
        }
    }

    // Event rows become visible in global_position order (AppendAdvisoryLockKey).
    private static async Task SerializePositionAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var takeLock = connection.CreateCommand();
        takeLock.Transaction = transaction;
        takeLock.CommandText = "SELECT pg_advisory_xact_lock(@key)";
        AddBigInt(takeLock, "key", AppendAdvisoryLockKey);
        await takeLock.ExecuteNonQueryAsync(ct);
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
