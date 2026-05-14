using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
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
public sealed class OutboxProcessor : BackgroundService
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExceptionBackoff = TimeSpan.FromSeconds(5);

    private readonly INpgsqlConnectionFactory _factory;
    private readonly IMessageDispatcher _dispatcher;
    private readonly EventTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly OutboxRetryPolicy _retryPolicy;
    private readonly OutboxProcessorOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(ct);
                if (processed == 0)
                {
                    await Task.Delay(IdlePollInterval, _options.TimeProvider, ct);
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
            "global_position " +
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
                GlobalPosition: reader.GetInt64(6)));
        }
        return rows;
    }

    private OutboxMessage HydrateMessage(PendingOutboxRow row)
    {
        var clrType = _registry.TypeFor(row.EventType);
        var payload = (IDomainEvent)JsonSerializer.Deserialize(
            row.PayloadJson, clrType, _jsonOptions)!;
        var metadata = JsonSerializer.Deserialize<EventMetadata>(
            row.MetadataJson, _jsonOptions)!;
        return new OutboxMessage(
            OutboxId: row.OutboxId,
            EventId: row.EventId,
            EventType: row.EventType,
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
    // rebinding them in C#. event_id UNIQUE on outbox_quarantine surfaces
    // a re-queue-then-fail-again as a unique violation, which is the right
    // signal for an operator.
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
            "  RETURNING outbox_id, event_id, event_type, payload, metadata, " +
            "            occurred_utc, attempt_count, last_error " +
            ") " +
            "INSERT INTO event_store.outbox_quarantine " +
            "  (outbox_id, event_id, event_type, payload, metadata, " +
            "   occurred_utc, attempt_count, final_error, quarantined_at) " +
            "SELECT outbox_id, event_id, event_type, payload, metadata, " +
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
        long GlobalPosition);
}
