using System.Data;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Data.SqlClient;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Hand-rolled SQL Server implementation of IDelayQueue (ADR 0017). ScheduleAsync writes a
// self-describing row: the command serialized to JSON with its registered type name, plus the
// dispatch metadata the processor needs to rebuild the command context. CancelAsync marks the
// pending rows for a stream and step cancelled. The due-row claim and dispatch live in
// SqlServerDelayQueueProcessor, not here.
//
// Duplicated from the PostgreSQL adapter per ADR 0004.
public sealed class SqlServerDelayQueue : IDelayQueue
{
    private readonly ISqlServerConnectionFactory _factory;
    private readonly CommandTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;

    public SqlServerDelayQueue(
        ISqlServerConnectionFactory factory,
        CommandTypeRegistry registry,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        _factory = factory;
        _registry = registry;
        _jsonOptions = jsonOptions;
    }

    public async Task ScheduleAsync(
        ICommand command,
        DateTimeOffset fireAtUtc,
        StreamId scheduledByStream,
        string scheduledByStep,
        EventMetadata causingEventMetadata,
        SystemActor actor,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(causingEventMetadata);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduledByStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var commandType = _registry.NameFor(command.GetType());
        var payloadJson = JsonSerializer.Serialize(command, command.GetType(), _jsonOptions);

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var cmd = new SqlCommand(
            "INSERT INTO event_store.delayed_commands " +
            "(tenant_id, fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step) " +
            "VALUES (@tenant_id, @fire_at_utc, @command_type, @command_payload, @correlation_id, " +
            "@causation_id, @actor_id, @service_name, @idempotency_key, @scheduled_by_stream_id, " +
            "@scheduled_by_step)",
            connection);
        AddUuid(cmd, "tenant_id", causingEventMetadata.Tenant.Value);
        AddDateTimeOffset(cmd, "fire_at_utc", fireAtUtc);
        AddIdentifier(cmd, "command_type", commandType);
        AddJson(cmd, "command_payload", payloadJson);
        AddUuid(cmd, "correlation_id", causingEventMetadata.CorrelationId);
        AddUuid(cmd, "causation_id", causingEventMetadata.EventId);
        AddUuid(cmd, "actor_id", actor.Id);
        AddIdentifier(cmd, "service_name", actor.ServiceName);
        AddIdentifier(cmd, "idempotency_key", idempotencyKey);
        AddIdentifier(cmd, "scheduled_by_stream_id", scheduledByStream.Value);
        AddIdentifier(cmd, "scheduled_by_step", scheduledByStep);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> CancelAsync(
        StreamId scheduledByStream,
        string scheduledByStep,
        string cancellationReason,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduledByStep);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancellationReason);

        await using var connection = await _factory.OpenConnectionAsync(ct);

        // READPAST on the update target, the counterpart of PostgreSQL's FOR UPDATE SKIP LOCKED in
        // the same cancel. A row another transaction holds is mid-dispatch (firing), and its own
        // compensation is what calls this cancel, so cancelling it would self-lock against the
        // dispatching transaction. Stepping over that row is the right answer: the zero-row result
        // means there was nothing to cancel because the timeout is already being delivered. An
        // unlocked future-dated row is locked and cancelled as before.
        await using var cmd = new SqlCommand(
            "UPDATE dc " +
            "SET cancelled_at_utc = SYSUTCDATETIME(), cancellation_reason = @reason " +
            "FROM event_store.delayed_commands AS dc WITH (UPDLOCK, READPAST, ROWLOCK) " +
            "WHERE dc.scheduled_by_stream_id = @stream_id " +
            "  AND dc.scheduled_by_step = @step " +
            "  AND dc.dispatched_at_utc IS NULL " +
            "  AND dc.cancelled_at_utc IS NULL",
            connection);
        AddText(cmd, "reason", cancellationReason);
        AddIdentifier(cmd, "stream_id", scheduledByStream.Value);
        AddIdentifier(cmd, "step", scheduledByStep);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static void AddUuid(SqlCommand cmd, string name, Guid value)
        => cmd.Parameters.Add("@" + name, SqlDbType.UniqueIdentifier).Value = value;

    // Identifier columns are VARCHAR and appear in predicates, so they bind VarChar to match and
    // keep the index seeks. See SqlServerEventStore for the full reasoning.
    private static void AddIdentifier(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.VarChar, 200).Value = value;

    // Free text and JSON bind NVARCHAR against the UTF-8 VARCHAR(MAX) columns. VarChar would
    // narrow the value to the connection codepage client-side and destroy non-ASCII silently.
    private static void AddJson(SqlCommand cmd, string name, string json)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = json;

    private static void AddText(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = value;

    private static void AddDateTimeOffset(SqlCommand cmd, string name, DateTimeOffset value)
        => cmd.Parameters.Add("@" + name, SqlDbType.DateTimeOffset).Value = value;
}
