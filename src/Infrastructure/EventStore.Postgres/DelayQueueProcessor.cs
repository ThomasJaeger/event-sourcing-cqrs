using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Drains due rows from event_store.delayed_commands and dispatches each as a
// command through ICausedCommandBus (ADR 0017). A deliberate concrete mirror of
// OutboxProcessor rather than a shared base class: the claim-dispatch-retry-
// quarantine loop is the shape both processors implement, and a shared base
// would hide it. The substantive differences from the outbox are the dispatch
// surface (ICausedCommandBus, not IMessageDispatcher), the due-row predicate
// (fire_at_utc <= now, where the outbox dispatches every pending row), and the
// per-row context reconstruction.
//
// The whole batch runs in a single NpgsqlTransaction with FOR UPDATE SKIP
// LOCKED, so the row lock is the claim: on crash, Postgres releases it and the
// row reverts to due with no cleanup code and no recovery sweeper. At-least-once
// redelivery is made effectively-once by the idempotency key every row carries,
// which IdempotencyBehavior deduplicates (ADR 0016) the way projection handlers
// dedupe redelivered events for the outbox.
//
// LISTEN/NOTIFY (migration 0008) gives a sub-second wake on inserts, though it
// carries less load than the outbox's: most rows are future-dated, so the idle
// timer fires them and the notification mainly shortens latency for at-or-near-
// present rows.
public sealed class DelayQueueProcessor : BackgroundService
{
    private static readonly TimeSpan ExceptionBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly INpgsqlConnectionFactory _factory;
    private readonly CommandTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ICausedCommandBus _causedCommandBus;
    private readonly DelayQueueRetryPolicy _retryPolicy;
    private readonly DelayQueueProcessorOptions _options;
    private readonly ILogger<DelayQueueProcessor> _logger;

    private TaskCompletionSource<bool> _notification =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _listenerCts;
    private NpgsqlConnection? _listenerConnection;
    private Task? _listenerTask;

    public DelayQueueProcessor(
        INpgsqlConnectionFactory factory,
        CommandTypeRegistry registry,
        JsonSerializerOptions jsonOptions,
        ICausedCommandBus causedCommandBus,
        DelayQueueRetryPolicy retryPolicy,
        IOptions<DelayQueueProcessorOptions> options,
        ILogger<DelayQueueProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(causedCommandBus);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _registry = registry;
        _jsonOptions = jsonOptions;
        _causedCommandBus = causedCommandBus;
        _retryPolicy = retryPolicy;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        _listenerCts = new CancellationTokenSource();
        await OpenListenerAsync(ct);
        _listenerTask = Task.Run(() => ListenAsync(_listenerCts.Token));
        await base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_listenerCts is not null)
        {
            await _listenerCts.CancelAsync();
            if (_listenerTask is not null)
            {
                try { await _listenerTask; }
                catch (OperationCanceledException) { }
            }
            _listenerCts.Dispose();
            _listenerCts = null;
            _listenerTask = null;
        }
        await DisposeListenerConnectionAsync();
        await base.StopAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcs = Volatile.Read(ref _notification);
                var processed = await ProcessBatchAsync(ct);
                if (processed == 0)
                {
                    await Task.WhenAny(
                        tcs.Task,
                        Task.Delay(_options.IdlePollInterval, _options.TimeProvider, ct));
                    Volatile.Write(
                        ref _notification,
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delay-queue batch failed; backing off");
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

    private async Task OpenListenerAsync(CancellationToken ct)
    {
        var connection = await _factory.OpenConnectionAsync(ct);
        connection.Notification += OnNotification;
        await using (var listenCmd = connection.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {QuoteIdentifier(_options.NotificationChannelName)}";
            await listenCmd.ExecuteNonQueryAsync(ct);
        }
        _listenerConnection = connection;
    }

    private async Task DisposeListenerConnectionAsync()
    {
        if (_listenerConnection is null)
        {
            return;
        }
        _listenerConnection.Notification -= OnNotification;
        try { await _listenerConnection.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Delay-queue listener connection dispose failed");
        }
        _listenerConnection = null;
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
        => Volatile.Read(ref _notification).TrySetResult(true);

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _listenerConnection!.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Delay-queue listener connection dropped; reconnecting in {Delay}",
                    ListenerReconnectDelay);
                await DisposeListenerConnectionAsync();
                try
                {
                    await Task.Delay(ListenerReconnectDelay, ct);
                    await OpenListenerAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception reconnectEx)
                {
                    _logger.LogError(
                        reconnectEx, "Delay-queue listener reconnect failed; will retry");
                    try { await Task.Delay(ListenerReconnectDelay, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                }
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    // Public so tests and tooling can drive a single batch outside the loop.
    // Drains up to BatchSize due rows in one transaction; returns the count
    // processed (dispatched or moved to failure/quarantine).
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var nowOffset = _options.TimeProvider.GetUtcNow();
        var nowUtc = nowOffset.UtcDateTime;

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var batch = await SelectDueAsync(connection, transaction, nowUtc, ct);
        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        foreach (var row in batch)
        {
            try
            {
                await DispatchAsync(row, nowUtc, ct);
                await MarkDispatchedAsync(connection, transaction, row.DelayedCommandId, nowUtc, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                var newAttemptCount = row.AttemptCount + 1;
                var nextAttempt = _retryPolicy
                    .ComputeNextAttempt(newAttemptCount, nowOffset, _options.Jitter())
                    .UtcDateTime;
                await RecordFailureAsync(
                    connection, transaction, row.DelayedCommandId,
                    newAttemptCount, ex.ToString(), nextAttempt, ct);

                if (newAttemptCount >= _options.MaxAttempts)
                {
                    await QuarantineAsync(connection, transaction, row.DelayedCommandId, nowUtc, ct);
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

    private async Task<List<DueDelayedCommandRow>> SelectDueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "SELECT delayed_command_id, command_type, command_payload, correlation_id, " +
            "causation_id, actor_id, service_name, idempotency_key, scheduled_by_stream_id, " +
            "scheduled_by_step, attempt_count, tenant_id " +
            "FROM event_store.delayed_commands " +
            "WHERE fire_at_utc <= @now " +
            "  AND dispatched_at_utc IS NULL " +
            "  AND cancelled_at_utc IS NULL " +
            "  AND (next_attempt_at IS NULL OR next_attempt_at <= @now) " +
            "ORDER BY fire_at_utc, delayed_command_id " +
            "LIMIT @batch_size " +
            "FOR UPDATE SKIP LOCKED";
        AddTimestampTz(cmd, "now", nowUtc);
        AddInteger(cmd, "batch_size", _options.BatchSize);

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

    private async Task DispatchAsync(DueDelayedCommandRow row, DateTime nowUtc, CancellationToken ct)
    {
        var clrType = _registry.TypeFor(row.CommandType);
        var command = (ICommand)JsonSerializer.Deserialize(row.CommandPayload, clrType, _jsonOptions)!;

        // The rebuilt metadata supplies CorrelationId, EventId, and Tenant to the
        // caused bus. Tenant comes from the stored row, so the resurfaced command
        // runs under the tenant that scheduled it, the same way CorrelationId and
        // EventId carry the trace and causation forward. The remaining fields
        // satisfy the EventMetadata constructor with the row's actor and a
        // placeholder occurred-time; the bus does not consume them (ADR 0017).
        var causingMetadata = new EventMetadata(
            EventId: row.CausationId,
            CorrelationId: row.CorrelationId,
            CausationId: Guid.Empty,
            ActorId: row.ActorId,
            Source: row.ServiceName,
            SchemaVersion: 1,
            OccurredUtc: nowUtc,
            Tenant: row.Tenant);
        var actor = new SystemActor(row.ActorId, row.ServiceName);

        await _causedCommandBus.SendAsync(command, causingMetadata, actor, row.IdempotencyKey, ct);
    }

    private static async Task MarkDispatchedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long delayedCommandId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "UPDATE event_store.delayed_commands SET dispatched_at_utc = @now " +
            "WHERE delayed_command_id = @id";
        AddTimestampTz(cmd, "now", nowUtc);
        AddBigInt(cmd, "id", delayedCommandId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long delayedCommandId,
        int attemptCount,
        string lastError,
        DateTime nextAttemptUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "UPDATE event_store.delayed_commands " +
            "SET attempt_count = @attempt_count, " +
            "    last_error = @last_error, " +
            "    next_attempt_at = @next_attempt_at " +
            "WHERE delayed_command_id = @id";
        AddInteger(cmd, "attempt_count", attemptCount);
        AddText(cmd, "last_error", lastError);
        AddTimestampTz(cmd, "next_attempt_at", nextAttemptUtc);
        AddBigInt(cmd, "id", delayedCommandId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Atomic CTE move, mirroring OutboxProcessor.QuarantineAsync. DELETE
    // ...RETURNING carries the row's columns and the just-recorded last_error
    // out of the live table rather than reading-then-rebinding in C#.
    private static async Task QuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long delayedCommandId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "WITH moved AS ( " +
            "  DELETE FROM event_store.delayed_commands " +
            "  WHERE delayed_command_id = @id " +
            "  RETURNING delayed_command_id, fire_at_utc, command_type, command_payload, " +
            "            correlation_id, causation_id, actor_id, service_name, idempotency_key, " +
            "            scheduled_by_stream_id, scheduled_by_step, attempt_count, last_error, tenant_id " +
            ") " +
            "INSERT INTO event_store.delayed_commands_quarantine " +
            "  (delayed_command_id, fire_at_utc, command_type, command_payload, correlation_id, " +
            "   causation_id, actor_id, service_name, idempotency_key, scheduled_by_stream_id, " +
            "   scheduled_by_step, attempt_count, final_error, quarantined_at, tenant_id) " +
            "SELECT delayed_command_id, fire_at_utc, command_type, command_payload, correlation_id, " +
            "       causation_id, actor_id, service_name, idempotency_key, scheduled_by_stream_id, " +
            "       scheduled_by_step, attempt_count, last_error, @now, tenant_id " +
            "FROM moved";
        AddBigInt(cmd, "id", delayedCommandId);
        AddTimestampTz(cmd, "now", nowUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddBigInt(NpgsqlCommand cmd, string name, long value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Bigint, value);

    private static void AddInteger(NpgsqlCommand cmd, string name, int value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Integer, value);

    private static void AddText(NpgsqlCommand cmd, string name, string value)
        => cmd.Parameters.AddWithValue(name, NpgsqlDbType.Text, value);

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
