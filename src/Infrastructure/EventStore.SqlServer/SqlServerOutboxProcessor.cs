using System.Data;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.Versioning;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventSourcingCqrs.Infrastructure.EventStore.SqlServer;

// Pattern from Chapter 8: the outbox processor drains pending events to the in-process bus, FIFO
// by outbox_id, with exponential-backoff retry and move-to-quarantine after MaxAttempts.
// Adapter-local per ADR 0004; the PostgreSQL adapter ships its own.
//
// The whole batch runs inside a single transaction. Rows are claimed with
// WITH (UPDLOCK, READPAST, ROWLOCK), the T-SQL counterpart of FOR UPDATE SKIP LOCKED: UPDLOCK
// takes the update lock, READPAST steps over rows another claimant already holds, ROWLOCK keeps
// the engine from escalating to a page lock and swallowing rows it should have skipped. The row
// lock IS the claim. There is no in-flight column, and on a crash the engine releases the lock
// and the row reverts to pending with no cleanup code.
//
// POLLING ONLY, and the absence is the design. The PostgreSQL processor carries a second wake
// path: a long-lived listener connection parked in WaitAsync, woken by a pg_notify trigger, with
// a TaskCompletionSource raced against the idle timer, plus reconnect handling and a re-entrant
// teardown to take it all down safely. SQL Server has no LISTEN/NOTIFY and PLAN.md:245 scopes
// this engine's trigger mechanism to polling in v1, so none of that machinery exists here. The
// idle poll is the entire wake path and IdlePollInterval is the dispatch-latency bound.
//
// That subtraction removes a whole class of bug rather than merely simplifying the file. With no
// listener there is no listener connection, no listener task, no listener cancellation source,
// and therefore nothing for a re-entrant StopAsync to race over. This processor overrides neither
// StartAsync nor StopAsync; BackgroundService's own cancellation of ExecuteAsync is the whole
// teardown.
public sealed class SqlServerOutboxProcessor : BackgroundService
{
    private static readonly TimeSpan ExceptionBackoff = TimeSpan.FromSeconds(5);

    private readonly ISqlServerConnectionFactory _factory;
    private readonly IMessageDispatcher _dispatcher;
    private readonly EventTypeRegistry _registry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SqlServerOutboxRetryPolicy _retryPolicy;
    private readonly SqlServerOutboxProcessorOptions _options;
    private readonly ILogger<SqlServerOutboxProcessor> _logger;

    public SqlServerOutboxProcessor(
        ISqlServerConnectionFactory factory,
        IMessageDispatcher dispatcher,
        EventTypeRegistry registry,
        JsonSerializerOptions jsonOptions,
        SqlServerOutboxRetryPolicy retryPolicy,
        IOptions<SqlServerOutboxProcessorOptions> options,
        ILogger<SqlServerOutboxProcessor> logger)
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
                    await Task.Delay(_options.IdlePollInterval, _options.TimeProvider, ct);
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

    // Public so AdminConsole tooling and tests can drive a single batch outside the background
    // loop. The contract is "drain up to BatchSize pending rows in one transaction; return the
    // count processed."
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var nowOffset = _options.TimeProvider.GetUtcNow();

        await using var connection = await _factory.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var batch = await SelectPendingAsync(connection, transaction, nowOffset, ct);
        if (batch.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return 0;
        }

        foreach (var row in batch)
        {
            try
            {
                await _dispatcher.DispatchAsync(HydrateMessage(row), ct);
                await MarkSentAsync(connection, transaction, row.OutboxId, nowOffset, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                // Increment-then-check-then-quarantine ordering matches Chapter 8. The UPDATE on
                // a row about to die is a microscopic waste; splitting the failure path in two to
                // avoid it is not worth the complexity.
                var newAttemptCount = row.AttemptCount + 1;
                var nextAttempt = _retryPolicy.ComputeNextAttempt(
                    newAttemptCount, nowOffset, _options.Jitter());
                await RecordFailureAsync(
                    connection, transaction, row.OutboxId,
                    newAttemptCount, ex.ToString(), nextAttempt, ct);

                if (newAttemptCount >= _options.MaxAttempts)
                {
                    await QuarantineAsync(connection, transaction, row.OutboxId, nowOffset, ct);
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

    // The claim. UPDLOCK takes the update lock the batch transaction will hold; READPAST steps
    // over rows a concurrent claimant already locked instead of blocking on them; ROWLOCK stops
    // the engine escalating to a page lock, which would make READPAST skip rows that were never
    // claimed. Together they are FOR UPDATE SKIP LOCKED in T-SQL, and the lock is the claim.
    private async Task<List<PendingOutboxRow>> SelectPendingAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT TOP (@batch_size) outbox_id, event_id, event_type, payload, metadata, " +
            "attempt_count, global_position " +
            "FROM event_store.outbox WITH (UPDLOCK, READPAST, ROWLOCK) " +
            "WHERE sent_utc IS NULL " +
            "  AND (next_attempt_at IS NULL OR next_attempt_at <= @now) " +
            "ORDER BY outbox_id",
            connection,
            transaction);
        AddInteger(cmd, "batch_size", _options.BatchSize);
        AddDateTimeOffset(cmd, "now", now);

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
        var metadata = EventMetadataReader.Read(row.MetadataJson, _jsonOptions);
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
        SqlConnection connection,
        SqlTransaction transaction,
        long outboxId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE event_store.outbox SET sent_utc = @now WHERE outbox_id = @outbox_id",
            connection,
            transaction);
        AddDateTimeOffset(cmd, "now", now);
        AddBigInt(cmd, "outbox_id", outboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RecordFailureAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long outboxId,
        int attemptCount,
        string lastError,
        DateTimeOffset nextAttempt,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE event_store.outbox " +
            "SET attempt_count = @attempt_count, " +
            "    last_error = @last_error, " +
            "    next_attempt_at = @next_attempt_at " +
            "WHERE outbox_id = @outbox_id",
            connection,
            transaction);
        AddInteger(cmd, "attempt_count", attemptCount);
        AddText(cmd, "last_error", lastError);
        AddDateTimeOffset(cmd, "next_attempt_at", nextAttempt);
        AddBigInt(cmd, "outbox_id", outboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The atomic move. PostgreSQL writes this as a data-modifying CTE
    // (WITH moved AS (DELETE ... RETURNING) INSERT ... SELECT FROM moved); T-SQL has no such CTE,
    // and its counterpart is DELETE ... OUTPUT DELETED.* INTO. Same property either way: the row
    // leaves the live outbox and arrives in quarantine in one statement, carrying attempt_count
    // and last_error structurally rather than being read back and rebound in C#.
    private static async Task QuarantineAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long outboxId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "DELETE FROM event_store.outbox " +
            "OUTPUT DELETED.outbox_id, DELETED.event_id, DELETED.event_type, DELETED.payload, " +
            "       DELETED.metadata, DELETED.occurred_utc, DELETED.attempt_count, " +
            "       DELETED.last_error, @now " +
            "  INTO event_store.outbox_quarantine " +
            "       (outbox_id, event_id, event_type, payload, metadata, occurred_utc, " +
            "        attempt_count, final_error, quarantined_at) " +
            "WHERE outbox_id = @outbox_id",
            connection,
            transaction);
        AddBigInt(cmd, "outbox_id", outboxId);
        AddDateTimeOffset(cmd, "now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddBigInt(SqlCommand cmd, string name, long value)
        => cmd.Parameters.Add("@" + name, SqlDbType.BigInt).Value = value;

    private static void AddInteger(SqlCommand cmd, string name, int value)
        => cmd.Parameters.Add("@" + name, SqlDbType.Int).Value = value;

    // last_error is VARCHAR(MAX) with the UTF-8 collation, and an exception's ToString can carry
    // any character a message or a path holds. It binds as NVARCHAR for the same reason the JSON
    // columns do: VarChar would narrow it to the connection codepage client-side.
    private static void AddText(SqlCommand cmd, string name, string value)
        => cmd.Parameters.Add("@" + name, SqlDbType.NVarChar, -1).Value = value;

    private static void AddDateTimeOffset(SqlCommand cmd, string name, DateTimeOffset value)
        => cmd.Parameters.Add("@" + name, SqlDbType.DateTimeOffset).Value = value;

    private readonly record struct PendingOutboxRow(
        long OutboxId,
        Guid EventId,
        string EventType,
        string PayloadJson,
        string MetadataJson,
        int AttemptCount,
        long GlobalPosition);
}
