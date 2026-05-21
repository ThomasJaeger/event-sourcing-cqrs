using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Hand-rolled PostgreSQL implementation of IDelayQueue (ADR 0017). ScheduleAsync
// writes a self-describing row: the command serialized to JSON with its
// registered type name, plus the dispatch metadata DelayQueueProcessor needs to
// rebuild the command context. CancelAsync marks the pending rows for a stream
// and step cancelled. The due-row claim and dispatch live in DelayQueueProcessor
// (the BackgroundService, commit 17), not here.
public sealed class PostgresDelayQueue : IDelayQueue
{
    private readonly INpgsqlConnectionFactory _factory;
    private readonly CommandTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;

    public PostgresDelayQueue(
        INpgsqlConnectionFactory factory,
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
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.delayed_commands " +
            "(fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step) " +
            "VALUES (@fire_at_utc, @command_type, @command_payload, @correlation_id, @causation_id, " +
            "@actor_id, @service_name, @idempotency_key, @scheduled_by_stream_id, @scheduled_by_step)";
        AddTimestampTz(cmd, "fire_at_utc", fireAtUtc.UtcDateTime);
        AddText(cmd, "command_type", commandType);
        AddJsonb(cmd, "command_payload", payloadJson);
        AddUuid(cmd, "correlation_id", causingEventMetadata.CorrelationId);
        AddUuid(cmd, "causation_id", causingEventMetadata.EventId);
        AddUuid(cmd, "actor_id", actor.Id);
        AddText(cmd, "service_name", actor.ServiceName);
        AddText(cmd, "idempotency_key", idempotencyKey);
        AddText(cmd, "scheduled_by_stream_id", scheduledByStream.Value);
        AddText(cmd, "scheduled_by_step", scheduledByStep);
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
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE event_store.delayed_commands " +
            "SET cancelled_at_utc = now(), cancellation_reason = @reason " +
            "WHERE scheduled_by_stream_id = @stream_id " +
            "  AND scheduled_by_step = @step " +
            "  AND dispatched_at_utc IS NULL " +
            "  AND cancelled_at_utc IS NULL";
        AddText(cmd, "reason", cancellationReason);
        AddText(cmd, "stream_id", scheduledByStream.Value);
        AddText(cmd, "step", scheduledByStep);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static void AddUuid(NpgsqlCommand cmd, string name, Guid value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);

    private static void AddText(NpgsqlCommand cmd, string name, string value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

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
