using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Hand-rolled SQL Server implementation of IEventStore. Structurally the twin of the PostgreSQL
// adapter and self-contained per ADR 0004: it shares no code with it, because every mechanic it
// depends on is engine-specific.
//
// AppendAsync writes the event row and its matching outbox row inside a single transaction:
// either both land or neither does, which is Phase 2's atomic-write done-when (PLAN.md:238). The
// outbox table is an implementation detail of this adapter; the public IEventStore surface does
// not mention it. AppendProcessManagerEventsAsync writes no outbox row (ADR 0013).
public sealed class SqlServerEventStore : IEventStore
{
    // sp_getapplock resource serializing global_position assignment against commit (ADR 0044).
    // IDENTITY hands out a position mid-transaction, so without this two appends can commit out
    // of position order and a reader tailing the log skips the one that commits late.
    //
    // The hazard is worse here than on PostgreSQL, not milder. Under SQL Server's default
    // lock-based READ COMMITTED a tailing reader BLOCKS on the uncommitted row rather than
    // reading past it, which looks safe. Turn on READ_COMMITTED_SNAPSHOT, which is the Azure SQL
    // default, and the reader skips exactly as PostgreSQL does and the events are lost. An
    // adapter without this lock would pass its tests on a default container and lose data in
    // production.
    //
    // Both append transactions take it as their first statement, before any position-drawing
    // INSERT, and the contract suite's held writer takes the same resource. Distinct lock space
    // from SqlServerMigrationRunner.MigrationLockResource.
    public const string AppendLockResource = "esrcq_event_append";

    private readonly ISqlServerConnectionFactory _factory;
    private readonly EventTypeRegistry _registry;
    private readonly ProcessManagerEventTypeRegistry _pmRegistry;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqlServerEventStore(
        ISqlServerConnectionFactory factory,
        EventTypeRegistry registry,
        ProcessManagerEventTypeRegistry pmRegistry,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pmRegistry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _registry = registry;
        _pmRegistry = pmRegistry;
        _jsonOptions = jsonOptions;
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        try
        {
            await SerializePositionAssignmentAsync(connection, transaction, ct);

            foreach (var envelope in events)
            {
                var eventTypeName = _registry.NameFor(envelope.Payload.GetType());
                var payloadJson = JsonSerializer.Serialize(
                    envelope.Payload, envelope.Payload.GetType(), _jsonOptions);
                var metadataJson = JsonSerializer.Serialize(envelope.Metadata, _jsonOptions);

                await using var insertEvent = new SqlCommand(
                    "INSERT INTO event_store.events " +
                    "(stream_id, stream_version, event_id, event_type, event_version, " +
                    "payload, metadata, occurred_utc) " +
                    "OUTPUT INSERTED.global_position " +
                    "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                    "@event_version, @payload, @metadata, @occurred_utc)",
                    connection,
                    transaction);
                AddIdentifier(insertEvent, "stream_id", envelope.StreamId.Value);
                AddInteger(insertEvent, "stream_version", envelope.StreamVersion);
                AddUuid(insertEvent, "event_id", envelope.EventId);
                AddIdentifier(insertEvent, "event_type", eventTypeName);
                AddSmallInt(insertEvent, "event_version", (short)envelope.EventVersion);
                AddJson(insertEvent, "payload", payloadJson);
                AddJson(insertEvent, "metadata", metadataJson);
                AddDateTimeOffset(insertEvent, "occurred_utc", envelope.OccurredUtc);

                // OUTPUT hands back the IDENTITY-assigned global_position, the counterpart of
                // PostgreSQL's RETURNING, so the matching outbox row can carry it without a
                // later read-back. OUTPUT without INTO fails on a table carrying any trigger:
                // the no-trigger rule on events and this readback are one decision.
                var globalPosition = (long)(await insertEvent.ExecuteScalarAsync(ct))!;

                // The outbox row, in the SAME transaction as the event row. Either both land or
                // neither does, which is Phase 2's atomic-write done-when (PLAN.md:238). The row
                // is self-describing: it copies the event's type, payload, metadata, and time,
                // and carries the position threaded out of the INSERT above, so the processor
                // never joins back into event_store.events to assemble a dispatch.
                //
                // sent_utc, next_attempt_at, last_error, and destination default to NULL;
                // attempt_count defaults to 0. Only the NOT NULL columns appear here.
                await using var insertOutbox = new SqlCommand(
                    "INSERT INTO event_store.outbox " +
                    "(event_id, event_type, payload, metadata, occurred_utc, global_position) " +
                    "VALUES (@event_id, @event_type, @payload, @metadata, @occurred_utc, " +
                    "@global_position)",
                    connection,
                    transaction);
                AddUuid(insertOutbox, "event_id", envelope.EventId);
                AddIdentifier(insertOutbox, "event_type", eventTypeName);
                AddJson(insertOutbox, "payload", payloadJson);
                AddJson(insertOutbox, "metadata", metadataJson);
                AddDateTimeOffset(insertOutbox, "occurred_utc", envelope.OccurredUtc);
                AddBigInt(insertOutbox, "global_position", globalPosition);
                await insertOutbox.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (SqlException ex) when (IsStreamVersionConflict(ex))
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
        await using var cmd = new SqlCommand(
            "SELECT global_position, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE stream_id = @stream_id AND stream_version > @from_version " +
            "ORDER BY stream_version",
            connection);
        AddIdentifier(cmd, "stream_id", streamId.Value);
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
            var occurredUtc = reader.GetDateTimeOffset(7).UtcDateTime;

            envelopes.Add(new EventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: eventVersion,
                Payload: DeserializeDomainEvent(eventType, payloadJson, streamId),
                Metadata: EventMetadataReader.Read(metadataJson, _jsonOptions),
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition));
        }

        return envelopes;
    }

    // Writes PM events to the same events table as AppendAsync, honoring the same
    // (stream_id, stream_version) constraint, but writes no outbox row: PM events are internal
    // coordination state, not published (ADR 0013).
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        try
        {
            await SerializePositionAssignmentAsync(connection, transaction, ct);

            foreach (var envelope in events)
            {
                var eventTypeName = _pmRegistry.NameFor(envelope.Payload.GetType());
                var payloadJson = JsonSerializer.Serialize(
                    envelope.Payload, envelope.Payload.GetType(), _jsonOptions);
                var metadataJson = JsonSerializer.Serialize(envelope.Metadata, _jsonOptions);

                await using var insertEvent = new SqlCommand(
                    "INSERT INTO event_store.events " +
                    "(stream_id, stream_version, event_id, event_type, event_version, " +
                    "payload, metadata, occurred_utc) " +
                    "VALUES (@stream_id, @stream_version, @event_id, @event_type, " +
                    "@event_version, @payload, @metadata, @occurred_utc)",
                    connection,
                    transaction);
                AddIdentifier(insertEvent, "stream_id", envelope.StreamId.Value);
                AddInteger(insertEvent, "stream_version", envelope.StreamVersion);
                AddUuid(insertEvent, "event_id", envelope.EventId);
                AddIdentifier(insertEvent, "event_type", eventTypeName);
                AddSmallInt(insertEvent, "event_version", (short)envelope.EventVersion);
                AddJson(insertEvent, "payload", payloadJson);
                AddJson(insertEvent, "metadata", metadataJson);
                AddDateTimeOffset(insertEvent, "occurred_utc", envelope.OccurredUtc);
                await insertEvent.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (SqlException ex) when (IsStreamVersionConflict(ex))
        {
            throw new ConcurrencyException(streamId, expectedVersion);
        }
    }

    // Reads a PM stream for rehydration. Requires a pm- prefixed StreamId (fail-loud per
    // ADR 0011/0013) and resolves payload types through the PM registry, never the aggregate one.
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
        await using var cmd = new SqlCommand(
            "SELECT global_position, stream_version, event_id, event_type, event_version, " +
            "payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE stream_id = @stream_id AND stream_version > @from_version " +
            "ORDER BY stream_version",
            connection);
        AddIdentifier(cmd, "stream_id", streamId.Value);
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
            var occurredUtc = reader.GetDateTimeOffset(7).UtcDateTime;

            envelopes.Add(new ProcessManagerEventEnvelope(
                StreamId: streamId,
                StreamVersion: streamVersion,
                EventId: eventId,
                EventType: eventType,
                EventVersion: eventVersion,
                Payload: DeserializeProcessManagerEvent(eventType, payloadJson, streamId),
                Metadata: EventMetadataReader.Read(metadataJson, _jsonOptions),
                OccurredUtc: occurredUtc,
                GlobalPosition: globalPosition));
        }

        return envelopes;
    }

    // Streams the events table excluding PM streams, in global_position order, yielding rows as
    // the reader produces them. SequentialAccess keeps the reader from buffering whole rows; the
    // single connection is held open for the enumeration's lifetime.
    //
    // No transaction and no isolation hint: the read runs at the database's configured level.
    // Under READ_COMMITTED_SNAPSHOT it takes a committed snapshot and never blocks on an
    // in-flight append, which is the posture ADR 0044's invariant is designed against.
    public async IAsyncEnumerable<EventEnvelope> ReadAllAsync(
        long fromPosition,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT global_position, stream_id, stream_version, event_id, event_type, " +
            "event_version, payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE global_position > @from_position " +
            "AND stream_id NOT LIKE 'pm-%' " +
            "ORDER BY global_position",
            connection);
        AddBigInt(cmd, "from_position", fromPosition);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            // ReadAsync only observes the token when it does real async I/O, so a buffered result
            // set would not see a cancellation between rows. Check explicitly each iteration so
            // enumeration stops deterministically.
            ct.ThrowIfCancellationRequested();

            yield return ReadFeedRow(reader);
        }
    }

    // Pattern from Chapter 13 (per-tenant rebuild): the tenant-scoped, ceilinged twin of
    // ReadAllAsync. Same feed and row mapping; the predicate adds the tenant and an inclusive
    // upper bound. tenant_id is the PERSISTED computed column from migration 0001.
    public async IAsyncEnumerable<EventEnvelope> ReadAllForTenantAsync(
        TenantId tenant,
        long fromPosition,
        long toPositionInclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT global_position, stream_id, stream_version, event_id, event_type, " +
            "event_version, payload, metadata, occurred_utc " +
            "FROM event_store.events " +
            "WHERE global_position > @from_position " +
            "AND global_position <= @to_position " +
            "AND tenant_id = @tenant " +
            "AND stream_id NOT LIKE 'pm-%' " +
            "ORDER BY global_position",
            connection);
        AddBigInt(cmd, "from_position", fromPosition);
        AddBigInt(cmd, "to_position", toPositionInclusive);
        AddUuid(cmd, "tenant", tenant.Value);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            yield return ReadFeedRow(reader);
        }
    }

    // SequentialAccess requires reading columns in ascending ordinal order.
    private EventEnvelope ReadFeedRow(SqlDataReader reader)
    {
        var globalPosition = reader.GetInt64(0);
        var streamId = StreamId.Parse(reader.GetString(1));
        var streamVersion = reader.GetInt32(2);
        var eventId = reader.GetGuid(3);
        var eventType = reader.GetString(4);
        var eventVersion = reader.GetInt16(5);
        var payloadJson = reader.GetString(6);
        var metadataJson = reader.GetString(7);
        var occurredUtc = reader.GetDateTimeOffset(8).UtcDateTime;

        return new EventEnvelope(
            StreamId: streamId,
            StreamVersion: streamVersion,
            EventId: eventId,
            EventType: eventType,
            EventVersion: eventVersion,
            Payload: DeserializeDomainEvent(eventType, payloadJson, streamId),
            Metadata: EventMetadataReader.Read(metadataJson, _jsonOptions),
            OccurredUtc: occurredUtc,
            GlobalPosition: globalPosition);
    }

    private IDomainEvent DeserializeDomainEvent(string eventType, string json, StreamId streamId)
    {
        Type clrType;
        try
        {
            clrType = _registry.TypeFor(eventType);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(eventType, streamId, ex);
        }
        return (IDomainEvent)JsonSerializer.Deserialize(json, clrType, _jsonOptions)!;
    }

    private IProcessManagerEvent DeserializeProcessManagerEvent(
        string eventType, string json, StreamId streamId)
    {
        Type clrType;
        try
        {
            clrType = _pmRegistry.TypeFor(eventType);
        }
        catch (UnknownEventTypeException ex)
        {
            throw new UnknownEventTypeException(eventType, streamId, ex);
        }
        return (IProcessManagerEvent)JsonSerializer.Deserialize(json, clrType, _jsonOptions)!;
    }

    // Event rows become visible in global_position commit order (AppendLockResource, ADR 0044).
    // Transaction scope, so the lock releases at commit or rollback with no cleanup code, which
    // is what pg_advisory_xact_lock gives the PostgreSQL adapter.
    private static async Task SerializePositionAssignmentAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var takeLock = new SqlCommand("sp_getapplock", connection, transaction)
        { CommandType = CommandType.StoredProcedure };
        takeLock.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = AppendLockResource;
        takeLock.Parameters.Add("@LockMode", SqlDbType.NVarChar, 32).Value = "Exclusive";
        takeLock.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Transaction";

        // Wait indefinitely, mirroring pg_advisory_xact_lock's blocking semantics. An append that
        // gave up on the lock would be an append that silently declined to serialize.
        takeLock.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = -1;

        var returnValue = takeLock.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        await takeLock.ExecuteNonQueryAsync(ct);

        // 0 granted immediately, 1 granted after waiting. Anything negative means the lock was
        // not taken, and appending without it would break the ADR 0044 invariant, so fail loudly
        // rather than write an unserialized position.
        var result = (int)returnValue.Value;
        if (result is not (0 or 1))
        {
            throw new InvalidOperationException(
                $"Could not acquire the append lock '{AppendLockResource}'. " +
                $"sp_getapplock returned {result}.");
        }
    }

    // The concurrency translation. SqlException carries no structured constraint-name field, the
    // way Npgsql's PostgresException.ConstraintName does, so the name filter is a substring match
    // on the message. That is the engine's whole vocabulary here, and it is why migration 0001
    // commits to named constraints rather than letting SQL Server generate names.
    //
    // 2627 is a unique CONSTRAINT violation, which is what the schema declares. 2601 is the same
    // collision expressed through a unique INDEX; it is covered defensively so an index-shaped
    // uniqueness added later cannot silently stop translating. A 2627 naming uq_events_event_id
    // is NOT a concurrency conflict and propagates untranslated: a reused event id is a
    // programming bug, not a retry condition.
    private static bool IsStreamVersionConflict(SqlException ex)
    {
        var primary = ex.Errors[0];
        return primary.Number is 2627 or 2601
            && primary.Message.Contains("uq_events_stream_version", StringComparison.Ordinal);
    }

    // Every string parameter carries a deliberate type. AddWithValue infers, and on this engine
    // an inferred type is a correctness question, not a style one.

    // Identifier columns (stream_id, event_type) are VARCHAR in the schema and ASCII by
    // construction: a StreamId is {prefix}:{guid:N} and an event type name is a CLR type name.
    // They bind as VarChar to MATCH the column. An NVARCHAR parameter compared against a VARCHAR
    // column forces SQL Server to convert the column, which turns an index seek into a scan, and
    // stream_id is the predicate on every stream read.
    private static void AddIdentifier(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.VarChar, 200).Value = value;

    // The JSON columns are the opposite case and the reasoning inverts. They are VARCHAR(MAX)
    // with a UTF-8 collation, and they bind as NVARCHAR. Binding them as VarChar would make the
    // client encode the string to the connection's single-byte codepage BEFORE it leaves the
    // process, destroying every non-ASCII character on the way out with no error and no warning.
    // NVARCHAR sends Unicode and lets the server convert into the UTF-8 column, which is lossless.
    // The type that looks right for the column is the one that corrupts the data.
    private static void AddJson(SqlCommand cmd, string name, string json)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = json;

    private static void AddUuid(SqlCommand cmd, string name, Guid value)
        => cmd.Parameters.Add("@" + name, SqlDbType.UniqueIdentifier).Value = value;

    private static void AddInteger(SqlCommand cmd, string name, int value)
        => cmd.Parameters.Add("@" + name, SqlDbType.Int).Value = value;

    private static void AddBigInt(SqlCommand cmd, string name, long value)
        => cmd.Parameters.Add("@" + name, SqlDbType.BigInt).Value = value;

    private static void AddSmallInt(SqlCommand cmd, string name, short value)
        => cmd.Parameters.Add("@" + name, SqlDbType.SmallInt).Value = value;

    // The schema column is DATETIMEOFFSET, so the value travels with an explicit zero offset
    // rather than as a naked local-or-UTC DateTime the driver has to guess about.
    private static void AddDateTimeOffset(SqlCommand cmd, string name, DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"Expected DateTimeKind.Utc on DATETIMEOFFSET parameter '{name}', got {utc.Kind}.",
                nameof(utc));
        }
        cmd.Parameters.Add("@" + name, SqlDbType.DateTimeOffset).Value =
            new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
