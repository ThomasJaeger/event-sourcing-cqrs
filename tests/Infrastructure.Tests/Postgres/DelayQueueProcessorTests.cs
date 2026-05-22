using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public class DelayQueueProcessorTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public DelayQueueProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dispatches_due_row_and_marks_dispatched()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        var id = await SeedDueRowAsync(dataSource, fireAt: BaseTime);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        bus.Received.Should().ContainSingle();
        bus.Received[0].Command.Should().BeOfType<TestTimeout>();
        var row = await ReadRowAsync(dataSource, id);
        row.DispatchedAtUtc.Should().Be(BaseTime.UtcDateTime);
        row.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Empty_queue_returns_zero_and_does_not_dispatch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        bus.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Future_dated_row_is_not_due_and_is_not_dispatched()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        var id = await SeedDueRowAsync(dataSource, fireAt: BaseTime.AddHours(1));

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        bus.Received.Should().BeEmpty();
        (await ReadRowAsync(dataSource, id)).DispatchedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Past_dated_row_is_dispatched_immediately()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        var id = await SeedDueRowAsync(dataSource, fireAt: BaseTime.AddSeconds(-5));

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        bus.Received.Should().ContainSingle();
        (await ReadRowAsync(dataSource, id)).DispatchedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_dispatch_increments_attempt_count_and_schedules_next_attempt()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        var id = await SeedDueRowAsync(dataSource, fireAt: BaseTime);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        var row = await ReadRowAsync(dataSource, id);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Contain("boom");
        row.DispatchedAtUtc.Should().BeNull();
        // attempt_count=1, jitter pinned to 1.0, so delay = 2^0 * 1.0 = 1 second.
        row.NextAttemptAt.Should().Be(BaseTime.UtcDateTime.AddSeconds(1));
    }

    [Fact]
    public async Task Exceeds_max_attempts_quarantines_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime), maxAttempts: 3);
        var id = await SeedDueRowAsync(dataSource, fireAt: BaseTime, attemptCount: 2);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        (await CountDelayedRowsAsync(dataSource)).Should().Be(0);
        var quarantined = await ReadQuarantineAsync(dataSource, id);
        quarantined.AttemptCount.Should().Be(3);
        quarantined.FinalError.Should().Contain("boom");
        quarantined.QuarantinedAt.Should().Be(BaseTime.UtcDateTime);
    }

    [Fact]
    public async Task Cancelled_row_is_not_claimed()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        await SeedDueRowAsync(dataSource, fireAt: BaseTime, cancelledAt: BaseTime.UtcDateTime);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        bus.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Already_dispatched_row_is_not_claimed()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        await SeedDueRowAsync(dataSource, fireAt: BaseTime, dispatchedAt: BaseTime.UtcDateTime);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        bus.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_processors_skip_locked_no_double_dispatch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus1 = new RecordingCausedCommandBus
        {
            Evaluate = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return null;
            },
        };
        var bus2 = new RecordingCausedCommandBus();
        var processor1 = BuildProcessor(dataSource, bus1, new FakeTimeProvider(BaseTime));
        var processor2 = BuildProcessor(dataSource, bus2, new FakeTimeProvider(BaseTime));
        await SeedDueRowAsync(dataSource, fireAt: BaseTime);

        // Processor 1 claims the row and holds the lock inside its open transaction.
        var task1 = processor1.ProcessBatchAsync(CancellationToken.None);
        await entered.Task;

        // Processor 2's SELECT FOR UPDATE SKIP LOCKED skips the locked row.
        var count2 = await processor2.ProcessBatchAsync(CancellationToken.None);
        release.TrySetResult();
        var count1 = await task1;

        count1.Should().Be(1);
        count2.Should().Be(0);
        bus1.Received.Should().ContainSingle();
        bus2.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Reconstructs_the_dispatch_context_from_the_row_columns()
    {
        // The row-to-metadata half of the round-trip. CausedCommandBusTests
        // (commit 10) pins the metadata-to-context half (CorrelationId and
        // EventId become the command context's CorrelationId and
        // CausationCommandId); together they pin row-to-context end-to-end.
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new RecordingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus, new FakeTimeProvider(BaseTime));
        var correlationId = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        await SeedDueRowAsync(
            dataSource, fireAt: BaseTime,
            correlationId: correlationId, causationId: causationId, idempotencyKey: "key-9");

        await processor.ProcessBatchAsync(CancellationToken.None);

        var record = bus.Received.Should().ContainSingle().Subject;
        record.Metadata.CorrelationId.Should().Be(correlationId);
        record.Metadata.EventId.Should().Be(causationId);
        record.Actor.Id.Should().Be(Pm.Id);
        record.Actor.ServiceName.Should().Be(Pm.ServiceName);
        record.IdempotencyKey.Should().Be("key-9");
    }

    private static readonly SystemActor Pm = SystemActors.OrderFulfillment;

    private static DelayQueueProcessor BuildProcessor(
        NpgsqlDataSource dataSource,
        RecordingCausedCommandBus bus,
        TimeProvider timeProvider,
        Func<double>? jitter = null,
        int maxAttempts = 10)
    {
        var options = Options.Create(new DelayQueueProcessorOptions
        {
            MaxAttempts = maxAttempts,
            TimeProvider = timeProvider,
            Jitter = jitter ?? (() => 1.0),
        });
        return new DelayQueueProcessor(
            new NpgsqlConnectionFactory(dataSource),
            new CommandTypeRegistry().Register<TestTimeout>(),
            CreateJsonOptions(),
            bus,
            new DelayQueueRetryPolicy(),
            options,
            NullLogger<DelayQueueProcessor>.Instance);
    }

    private static async Task<long> SeedDueRowAsync(
        NpgsqlDataSource dataSource,
        DateTimeOffset fireAt,
        int attemptCount = 0,
        Guid? correlationId = null,
        Guid? causationId = null,
        string idempotencyKey = "key-1",
        DateTime? dispatchedAt = null,
        DateTime? cancelledAt = null)
    {
        var payloadJson = JsonSerializer.Serialize(
            new TestTimeout(Guid.NewGuid()), typeof(TestTimeout), CreateJsonOptions());

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.delayed_commands " +
            "(fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step, " +
            "attempt_count, dispatched_at_utc, cancelled_at_utc) " +
            "VALUES (@fire_at, @type, @payload, @correlation, @causation, @actor, @service, " +
            "@key, @stream, @step, @attempts, @dispatched, @cancelled) " +
            "RETURNING delayed_command_id";
        cmd.Parameters.AddWithValue("fire_at", NpgsqlDbType.TimestampTz, fireAt.UtcDateTime);
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Text, nameof(TestTimeout));
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, correlationId ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("causation", NpgsqlDbType.Uuid, causationId ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, Pm.Id);
        cmd.Parameters.AddWithValue("service", NpgsqlDbType.Text, Pm.ServiceName);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, idempotencyKey);
        cmd.Parameters.AddWithValue("stream", NpgsqlDbType.Text, $"pm-order-fulfillment:{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, "await-payment");
        cmd.Parameters.AddWithValue("attempts", NpgsqlDbType.Integer, attemptCount);
        cmd.Parameters.AddWithValue("dispatched", NpgsqlDbType.TimestampTz, (object?)dispatchedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cancelled", NpgsqlDbType.TimestampTz, (object?)cancelledAt ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<DelayedRowState> ReadRowAsync(NpgsqlDataSource dataSource, long id)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT attempt_count, dispatched_at_utc, next_attempt_at, last_error, cancelled_at_utc " +
            "FROM event_store.delayed_commands WHERE delayed_command_id = @id";
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Delayed command row {id} not found");
        }
        return new DelayedRowState(
            AttemptCount: reader.GetInt32(0),
            DispatchedAtUtc: reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            NextAttemptAt: reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            LastError: reader.IsDBNull(3) ? null : reader.GetString(3),
            CancelledAtUtc: reader.IsDBNull(4) ? null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    private static async Task<long> CountDelayedRowsAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_store.delayed_commands";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<QuarantineRowState> ReadQuarantineAsync(
        NpgsqlDataSource dataSource, long delayedCommandId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT attempt_count, final_error, quarantined_at " +
            "FROM event_store.delayed_commands_quarantine WHERE delayed_command_id = @id";
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, delayedCommandId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Quarantine row for delayed_command_id {delayedCommandId} not found");
        }
        return new QuarantineRowState(
            AttemptCount: reader.GetInt32(0),
            FinalError: reader.GetString(1),
            QuarantinedAt: DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    private sealed record TestTimeout(Guid OrderId) : ICommand;

    private sealed class RecordingCausedCommandBus : ICausedCommandBus
    {
        public List<DispatchRecord> Received { get; } = [];
        public Func<ICommand, CancellationToken, Task<Exception?>>? Evaluate { get; set; }

        public async Task SendAsync(
            ICommand command,
            EventMetadata causingEventMetadata,
            SystemActor actor,
            string? idempotencyKey,
            CancellationToken ct)
        {
            Received.Add(new DispatchRecord(command, causingEventMetadata, actor, idempotencyKey));
            if (Evaluate is not null)
            {
                var failure = await Evaluate(command, ct);
                if (failure is not null)
                {
                    throw failure;
                }
            }
        }

        public sealed record DispatchRecord(
            ICommand Command, EventMetadata Metadata, SystemActor Actor, string? IdempotencyKey);
    }

    private readonly record struct DelayedRowState(
        int AttemptCount,
        DateTime? DispatchedAtUtc,
        DateTime? NextAttemptAt,
        string? LastError,
        DateTime? CancelledAtUtc);

    private readonly record struct QuarantineRowState(
        int AttemptCount,
        string FinalError,
        DateTime QuarantinedAt);
}
