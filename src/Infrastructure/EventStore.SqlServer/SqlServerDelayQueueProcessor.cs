using System.Data;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Claims due delayed commands and dispatches them through ICausedCommandBus (ADR 0014, ADR 0017),
// with exponential-backoff retry and move-to-quarantine after MaxAttempts. Adapter-local per
// ADR 0004; the PostgreSQL adapter ships its own.
//
// A deliberate concrete mirror of SqlServerOutboxProcessor, exactly as the PostgreSQL delay-queue
// processor mirrors its outbox processor. Same claim, same failure ladder, different table and a
// different dispatch surface.
//
// The whole batch runs in one transaction. Rows are claimed with WITH (UPDLOCK, READPAST, ROWLOCK),
// the T-SQL counterpart of FOR UPDATE SKIP LOCKED. The row lock IS the claim: there is no
// claimed_at column, and a crash releases the locks and reverts the rows to pending with no
// cleanup code.
//
// POLLING ONLY, and the absence is the design. SQL Server has no LISTEN/NOTIFY and PLAN.md's Phase 2 out-of-scope entry for engine-native SQL Server triggers
// scopes this engine to polling in v1, so the notification arm the PostgreSQL processor carries
// (listener connection, TaskCompletionSource wake, reconnect handling, re-entrant teardown) does
// not exist here. With no listener there is nothing for a re-entrant StopAsync to race over, so
// this processor overrides neither StartAsync nor StopAsync and BackgroundService's cancellation of
// ExecuteAsync is the whole teardown.
public sealed class SqlServerDelayQueueProcessor : BackgroundService
{
    private static readonly TimeSpan ExceptionBackoff = TimeSpan.FromSeconds(5);

    private readonly ISqlServerConnectionFactory _factory;
    private readonly ICausedCommandBus _causedCommandBus;
    private readonly CommandTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SqlServerDelayQueueRetryPolicy _retryPolicy;
    private readonly SqlServerDelayQueueProcessorOptions _options;
    private readonly ILogger<SqlServerDelayQueueProcessor> _logger;

    public SqlServerDelayQueueProcessor(
        ISqlServerConnectionFactory factory,
        ICausedCommandBus causedCommandBus,
        CommandTypeRegistry registry,
        JsonSerializerOptions jsonOptions,
        SqlServerDelayQueueRetryPolicy retryPolicy,
        IOptions<SqlServerDelayQueueProcessorOptions> options,
        ILogger<SqlServerDelayQueueProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(causedCommandBus);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _causedCommandBus = causedCommandBus;
        _registry = registry;
        _jsonOptions = jsonOptions;
        _retryPolicy = retryPolicy;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(ct);
                if (processed == 0)
                {
                    await Task.Delay(_options.IdlePollInterval, _options.TimeProvider, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delay queue batch failed; backing off");
                try
                {
                    await Task.Delay(ExceptionBackoff, _options.TimeProvider, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    // Public so tooling and tests can drive a single batch outside the background loop. The
    // contract is "dispatch up to BatchSize due rows in one transaction; return the count."
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var now = _options.TimeProvider.GetUtcNow();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var batch = await SelectDueAsync(connection, transaction, now, ct);
        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        foreach (var row in batch)
        {
            try
            {
                await DispatchAsync(row, now, ct);
                await MarkDispatchedAsync(connection, transaction, row.DelayedCommandId, now, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                var newAttemptCount = row.AttemptCount + 1;
                var nextAttempt = _retryPolicy.ComputeNextAttempt(
                    newAttemptCount, now, _options.Jitter());
                await RecordFailureAsync(
                    connection, transaction, row.DelayedCommandId,
                    newAttemptCount, ex.ToString(), nextAttempt, ct);

                if (newAttemptCount >= _options.MaxAttempts)
                {
                    await QuarantineAsync(connection, transaction, row.DelayedCommandId, now, ct);
                    _logger.LogCritical(
                        "Delayed command quarantined after {MaxAttempts} attempts. " +
                        "DelayedCommandId={DelayedCommandId} CommandType={CommandType} " +
                        "ScheduledByStream={Stream} Step={Step} LastError={LastError}",
                        _options.MaxAttempts, row.DelayedCommandId, row.CommandType,
                        row.ScheduledByStreamId, row.ScheduledByStep, ex.Message);
                }
                else
                {
                    _logger.LogWarning(
                        "Delayed command dispatch failed; will retry. " +
                        "DelayedCommandId={DelayedCommandId} CommandType={CommandType} " +
                        "AttemptCount={AttemptCount} NextAttemptAt={NextAttempt}",
                        row.DelayedCommandId, row.CommandType, newAttemptCount, nextAttempt);
                }
            }
        }

        await transaction.CommitAsync(ct);
        return batch.Count;
    }

    // Due, not dispatched, not cancelled, and past any backoff. Ordered by fire time then id so a
    // timeout that came due earlier goes first and the order is total.
    private async Task<List<DueDelayedCommandRow>> SelectDueAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP (@batch_size) delayed_command_id, command_type, command_payload, " +
            "correlation_id, causation_id, actor_id, service_name, idempotency_key, " +
            "scheduled_by_stream_id, scheduled_by_step, attempt_count, tenant_id " +
            "FROM event_store.delayed_commands WITH (UPDLOCK, READPAST, ROWLOCK) " +
            "WHERE fire_at_utc <= @now " +
            "  AND dispatched_at_utc IS NULL " +
            "  AND cancelled_at_utc IS NULL " +
            "  AND (next_attempt_at IS NULL OR next_attempt_at <= @now) " +
            "ORDER BY fire_at_utc, delayed_command_id",
            connection,
            transaction);
        AddInteger(cmd, "batch_size", _options.BatchSize);
        AddDateTimeOffset(cmd, "now", now);

        var rows = new List<DueDelayedCommandRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DueDelayedCommandRow(
                DelayedCommandId: reader.GetInt64(0),
                CommandType: reader.GetString(1),
                CommandPayload: reader.GetString(2),
                CorrelationId: reader.GetGuid(3),
                CausationId: reader.GetGuid(4),
                ActorId: reader.GetGuid(5),
                ServiceName: reader.GetString(6),
                IdempotencyKey: reader.GetString(7),
                ScheduledByStreamId: reader.GetString(8),
                ScheduledByStep: reader.GetString(9),
                AttemptCount: reader.GetInt32(10),
                Tenant: TenantId.From(reader.GetGuid(11))));
        }
        return rows;
    }

    private async Task DispatchAsync(DueDelayedCommandRow row, DateTimeOffset now, CancellationToken ct)
    {
        var clrType = _registry.TypeFor(row.CommandType);
        var command = (ICommand)JsonSerializer.Deserialize(row.CommandPayload, clrType, _jsonOptions)!;

        // The rebuilt metadata supplies CorrelationId, EventId, and Tenant to the caused bus.
        // Tenant comes from the stored row, so the resurfaced command runs under the tenant that
        // scheduled it, the same way CorrelationId and EventId carry the trace and causation
        // forward. The remaining fields satisfy the EventMetadata constructor with the row's actor
        // and a placeholder occurred-time; the bus does not consume them (ADR 0017).
        var causingMetadata = new EventMetadata(
            EventId: row.CausationId,
            CorrelationId: row.CorrelationId,
            CausationId: Guid.Empty,
            ActorId: row.ActorId,
            Source: row.ServiceName,
            OccurredUtc: now.UtcDateTime,
            Tenant: row.Tenant);
        var actor = new SystemActor(row.ActorId, row.ServiceName);

        await _causedCommandBus.SendAsync(command, causingMetadata, actor, row.IdempotencyKey, ct);
    }

    private static async Task MarkDispatchedAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long delayedCommandId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE event_store.delayed_commands SET dispatched_at_utc = @now " +
            "WHERE delayed_command_id = @id",
            connection,
            transaction);
        AddDateTimeOffset(cmd, "now", now);
        AddBigInt(cmd, "id", delayedCommandId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordFailureAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long delayedCommandId,
        int attemptCount,
        string lastError,
        DateTimeOffset nextAttempt,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE event_store.delayed_commands " +
            "SET attempt_count = @attempt_count, " +
            "    last_error = @last_error, " +
            "    next_attempt_at = @next_attempt_at " +
            "WHERE delayed_command_id = @id",
            connection,
            transaction);
        AddInteger(cmd, "attempt_count", attemptCount);
        AddText(cmd, "last_error", lastError);
        AddDateTimeOffset(cmd, "next_attempt_at", nextAttempt);
        AddBigInt(cmd, "id", delayedCommandId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The atomic move, mirroring SqlServerOutboxProcessor.QuarantineAsync. PostgreSQL writes this
    // as a data-modifying CTE; T-SQL has none, and DELETE ... OUTPUT DELETED.* INTO is the
    // counterpart. Either way the row leaves the live table and arrives in quarantine in one
    // statement, carrying attempt_count and the just-recorded last_error structurally rather than
    // being read back and rebound in C#.
    private static async Task QuarantineAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long delayedCommandId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "DELETE FROM event_store.delayed_commands " +
            "OUTPUT DELETED.delayed_command_id, DELETED.tenant_id, DELETED.fire_at_utc, " +
            "       DELETED.command_type, DELETED.command_payload, DELETED.correlation_id, " +
            "       DELETED.causation_id, DELETED.actor_id, DELETED.service_name, " +
            "       DELETED.idempotency_key, DELETED.scheduled_by_stream_id, " +
            "       DELETED.scheduled_by_step, DELETED.attempt_count, DELETED.last_error, @now " +
            "  INTO event_store.delayed_commands_quarantine " +
            "       (delayed_command_id, tenant_id, fire_at_utc, command_type, command_payload, " +
            "        correlation_id, causation_id, actor_id, service_name, idempotency_key, " +
            "        scheduled_by_stream_id, scheduled_by_step, attempt_count, final_error, " +
            "        quarantined_at) " +
            "WHERE delayed_command_id = @id",
            connection,
            transaction);
        AddBigInt(cmd, "id", delayedCommandId);
        AddDateTimeOffset(cmd, "now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddBigInt(SqlCommand cmd, string name, long value)
        => cmd.Parameters.Add("@" + name, SqlDbType.BigInt).Value = value;

    private static void AddInteger(SqlCommand cmd, string name, int value)
        => cmd.Parameters.Add("@" + name, SqlDbType.Int).Value = value;

    private static void AddText(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = value;

    private static void AddDateTimeOffset(SqlCommand cmd, string name, DateTimeOffset value)
        => cmd.Parameters.Add("@" + name, SqlDbType.DateTimeOffset).Value = value;

    private readonly record struct DueDelayedCommandRow(
        long DelayedCommandId,
        string CommandType,
        string CommandPayload,
        Guid CorrelationId,
        Guid CausationId,
        Guid ActorId,
        string ServiceName,
        string IdempotencyKey,
        string ScheduledByStreamId,
        string ScheduledByStep,
        int AttemptCount,
        TenantId Tenant);
}
