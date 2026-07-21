using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Pattern from Chapter 8: outbox processor drains pending events to the
// in-process bus, FIFO by outbox_id, with exponential-backoff retry and
// move-to-quarantine after MaxAttempts. Adapter-local per ADR 0004; the
// SQL Server adapter ships a parallel implementation in its own project.
//
// The whole batch runs inside a single NpgsqlTransaction. Rows are
// selected with FOR UPDATE SKIP LOCKED so accidental parallel processors
// don't double-dispatch. The row lock substitutes for an explicit in-flight
// column; on crash, Postgres releases the lock and the row reverts to
// pending without cleanup code.
//
// LISTEN/NOTIFY (migration 0005) gives the processor a sub-second wake on
// new outbox rows. A long-lived listener connection sits parked in
// NpgsqlConnection.WaitAsync; on notify, the OnNotification handler
// completes the current TaskCompletionSource. ExecuteAsync snapshots the
// TCS before each batch and awaits either it or an IdlePollInterval timer
// fallback. The timer keeps the processor honest if the listener
// connection drops or the trigger is disabled in a test.
public sealed class OutboxProcessor : BackgroundService
{
    private static readonly TimeSpan ExceptionBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly INpgsqlConnectionFactory _factory;
    private readonly IMessageDispatcher _dispatcher;
    private readonly EventTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly OutboxRetryPolicy _retryPolicy;
    private readonly OutboxProcessorOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

    // Listener-side state. _notification is the wake signal: the listener
    // calls TrySetResult on whatever instance is current at the time of the
    // notification, ExecuteAsync snapshots-then-swaps. Volatile.Read/Write
    // serialize the publish ordering between the two threads.
    private TaskCompletionSource<bool> _notification =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _listenerCts;
    private NpgsqlConnection? _listenerConnection;
    private Task? _listenerTask;

    public OutboxProcessor(
        INpgsqlConnectionFactory factory,
        IMessageDispatcher dispatcher,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions,
        OutboxRetryPolicy retryPolicy,
        IOptions<OutboxProcessorOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _dispatcher = dispatcher;
        _registry = registry;
        _jsonOptions = jsonOptions;
        _retryPolicy = retryPolicy;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        var listenerCts = new CancellationTokenSource();
        var listenerToken = listenerCts.Token;
        _listenerCts = listenerCts;
        await OpenListenerAsync(ct);
        // Long-running task: never awaited here. StopAsync cancels the
        // listener cts and awaits the task to drain.
        //
        // The token is read off the local, not the field. StopAsync claims _listenerCts the
        // moment it is entered, and this lambda does not run until the thread pool picks it
        // up, so a shutdown that lands in that gap would leave the lambda reading a field
        // that is already null. Capturing the token here means the listener starts from the
        // source that StartAsync created, whatever shutdown does to the field behind it.
        _listenerTask = Task.Run(() => ListenAsync(listenerToken));
        await base.StartAsync(ct);
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        // Cancel and drain the listener before the base class cancels and
        // drains ExecuteAsync. Both sides tolerate either ordering, but
        // shutting the listener first means the notification handler stops
        // firing before the processor loop exits.
        //
        // Claim the listener state before the first await, because shutdown is re-entrant:
        // the host's disposal path enters StopAsync more than once. Reading the fields, then
        // awaiting, then writing them back leaves a window where a second entry walks past a
        // null check the first entry has not yet acted on, and the loser dereferences a field
        // the winner already nulled. Exchanging both fields to null up front makes exactly one
        // entry the owner of the teardown; every other entry sees null and no-ops.
        var listenerCts = Interlocked.Exchange(ref _listenerCts, null);
        var listenerTask = Interlocked.Exchange(ref _listenerTask, null);
        if (listenerCts is not null)
        {
            await listenerCts.CancelAsync();
            if (listenerTask is not null)
            {
                try { await listenerTask; }
                catch (OperationCanceledException) { }
            }
            listenerCts.Dispose();
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
                // Snapshot BEFORE the batch: notifications arriving during
                // ProcessBatchAsync land on the snapshotted TCS, so the
                // subsequent WhenAny returns immediately. The TCS is swapped
                // for a fresh one after the wait completes.
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
                _logger.LogError(ex, "Outbox batch failed; backing off");
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
            // Channel name is identifier-quoted so an option override with a
            // non-default name (mixed case, embedded quotes) round-trips
            // through PostgreSQL's identifier parsing.
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
            _logger.LogWarning(ex, "Outbox listener connection dispose failed");
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
                // The listener connection dropped. While we're down, the
                // processor's idle timer is the only wake source; rows are
                // not lost, just delayed by at most IdlePollInterval.
                _logger.LogWarning(
                    ex, "Outbox listener connection dropped; reconnecting in {Delay}",
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
                    // If the reconnect itself fails, fall through and let the
                    // next iteration retry. The NRE on WaitAsync brings us
                    // back into this branch; a brief delay keeps the retry
                    // loop from spinning on a hard failure.
                    _logger.LogError(
                        reconnectEx, "Outbox listener reconnect failed; will retry");
                    try { await Task.Delay(ListenerReconnectDelay, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                }
            }
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    // Public so AdminConsole tooling and tests can drive a single batch
    // outside the background loop. The contract is "drain up to BatchSize
    // pending rows in one transaction; return the count processed."
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var nowOffset = _options.TimeProvider.GetUtcNow();
        var nowUtc = nowOffset.UtcDateTime;

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var batch = await SelectPendingAsync(connection, transaction, nowUtc, ct);
        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        foreach (var row in batch)
        {
            try
            {
                var message = HydrateMessage(row);
                await _dispatcher.DispatchAsync(message, ct);
                await MarkSentAsync(connection, transaction, row.OutboxId, nowUtc, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                // Increment-then-check-then-quarantine ordering matches Chapter 8.
                // The UPDATE on a row about to die is a microscopic waste; splitting
                // the failure path in two to avoid it isn't worth the complexity.
                var newAttemptCount = row.AttemptCount + 1;
                var nextAttempt = _retryPolicy
                    .ComputeNextAttempt(newAttemptCount, nowOffset, _options.Jitter())
                    .UtcDateTime;
                await RecordFailureAsync(
                    connection, transaction, row.OutboxId,
                    newAttemptCount, ex.ToString(), nextAttempt, ct);

                if (newAttemptCount >= _options.MaxAttempts)
                {
                    await QuarantineAsync(connection, transaction, row.OutboxId, nowUtc, ct);
                    _logger.LogCritical(
                        "Outbox message quarantined after {MaxAttempts} attempts. " +
                        "OutboxId={OutboxId} EventId={EventId} EventType={EventType} " +
                        "LastError={LastError}",
                        _options.MaxAttempts, row.OutboxId, row.EventId, row.EventType, ex.Message);
                }
                else
                {
                    _logger.LogWarning(
                        "Outbox dispatch failed; will retry. " +
                        "OutboxId={OutboxId} EventId={EventId} EventType={EventType} " +
                        "AttemptCount={AttemptCount} NextAttemptAt={NextAttempt}",
                        row.OutboxId, row.EventId, row.EventType, newAttemptCount, nextAttempt);
                }
            }
        }

        await transaction.CommitAsync(ct);
        return batch.Count;
    }

    private async Task<List<PendingOutboxRow>> SelectPendingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "SELECT outbox_id, event_id, event_type, payload, metadata, attempt_count, " +
            "global_position, event_version " +
            "FROM event_store.outbox " +
            "WHERE sent_utc IS NULL " +
            "  AND (next_attempt_at IS NULL OR next_attempt_at <= @now) " +
            "ORDER BY outbox_id " +
            "LIMIT @batch_size " +
            "FOR UPDATE SKIP LOCKED";
        AddTimestampTz(cmd, "now", nowUtc);
        AddInteger(cmd, "batch_size", _options.BatchSize);

        var rows = new List<PendingOutboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PendingOutboxRow(
                OutboxId: reader.GetInt64(0),
                EventId: reader.GetGuid(1),
                EventType: reader.GetString(2),
                PayloadJson: reader.GetString(3),
                MetadataJson: reader.GetString(4),
                AttemptCount: reader.GetInt32(5),
                GlobalPosition: reader.GetInt64(6),
                EventVersion: reader.GetInt16(7)));
        }
        return rows;
    }

    private OutboxMessage HydrateMessage(PendingOutboxRow row)
    {
        var clrType = _registry.TypeFor(row.EventType);
        var payload = (IDomainEvent)JsonSerializer.Deserialize(
            row.PayloadJson, clrType, _jsonOptions)!;
        var metadata = EventMetadataReader.Read(row.MetadataJson, _jsonOptions);
        return new OutboxMessage(
            OutboxId: row.OutboxId,
            EventId: row.EventId,
            EventType: row.EventType,
            EventVersion: row.EventVersion,
            Event: payload,
            Metadata: metadata,
            GlobalPosition: row.GlobalPosition,
            AttemptCount: row.AttemptCount);
    }

    private static async Task MarkSentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long outboxId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "UPDATE event_store.outbox SET sent_utc = @now WHERE outbox_id = @outbox_id";
        AddTimestampTz(cmd, "now", nowUtc);
        AddBigInt(cmd, "outbox_id", outboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordFailureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long outboxId,
        int attemptCount,
        string lastError,
        DateTime nextAttemptUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "UPDATE event_store.outbox " +
            "SET attempt_count = @attempt_count, " +
            "    last_error = @last_error, " +
            "    next_attempt_at = @next_attempt_at " +
            "WHERE outbox_id = @outbox_id";
        AddInteger(cmd, "attempt_count", attemptCount);
        AddText(cmd, "last_error", lastError);
        AddTimestampTz(cmd, "next_attempt_at", nextAttemptUtc);
        AddBigInt(cmd, "outbox_id", outboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Atomic CTE move. DELETE...RETURNING structurally carries attempt_count
    // and last_error out of the live outbox row rather than reading-then-
    // rebinding them in C#.
    //
    // outbox_quarantine carries pk_outbox_quarantine and nothing else: no unique
    // constraint on event_id, and no foreign key back to the live outbox, because
    // the live row is deleted on the move and an FK would block pruning. Nothing
    // re-queues a quarantined row today; a re-queue path is an operator tool this
    // repository has not built, and whatever builds it decides then whether a
    // second quarantine of the same event should collide or accumulate.
    private static async Task QuarantineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long outboxId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "WITH moved AS ( " +
            "  DELETE FROM event_store.outbox " +
            "  WHERE outbox_id = @outbox_id " +
            "  RETURNING outbox_id, event_id, event_type, event_version, payload, metadata, " +
            "            occurred_utc, attempt_count, last_error " +
            ") " +
            "INSERT INTO event_store.outbox_quarantine " +
            "  (outbox_id, event_id, event_type, event_version, payload, metadata, " +
            "   occurred_utc, attempt_count, final_error, quarantined_at) " +
            "SELECT outbox_id, event_id, event_type, event_version, payload, metadata, " +
            "       occurred_utc, attempt_count, last_error, @now " +
            "FROM moved";
        AddBigInt(cmd, "outbox_id", outboxId);
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

    private readonly record struct PendingOutboxRow(
        long OutboxId,
        Guid EventId,
        string EventType,
        string PayloadJson,
        string MetadataJson,
        int AttemptCount,
        long GlobalPosition,
        int EventVersion);
}
