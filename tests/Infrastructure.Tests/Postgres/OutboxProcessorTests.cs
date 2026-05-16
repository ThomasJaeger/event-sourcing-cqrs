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

public class OutboxProcessorTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public OutboxProcessorTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Drains_pending_row_marks_sent()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher, time);
        var eventId = Guid.NewGuid();
        var payload = new TestPayload(Guid.NewGuid(), 12.5m);
        var outboxId = await SeedOutboxRowAsync(dataSource, eventId, payload, globalPosition: 99);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        dispatcher.Received.Should().HaveCount(1);
        dispatcher.Received[0].EventId.Should().Be(eventId);
        dispatcher.Received[0].OutboxId.Should().Be(outboxId);
        dispatcher.Received[0].Event.Should().BeEquivalentTo(payload);
        dispatcher.Received[0].GlobalPosition.Should().Be(99);
        var row = await ReadRowAsync(dataSource, outboxId);
        row.SentUtc.Should().Be(BaseTime.UtcDateTime);
        row.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Empty_outbox_returns_zero_and_does_not_call_dispatcher()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher, time);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(0);
        dispatcher.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_dispatch_increments_attempt_count_schedules_next_attempt_persists_last_error()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        var processor = BuildProcessor(dataSource, dispatcher, time);
        var outboxId = await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 1m));

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        var row = await ReadRowAsync(dataSource, outboxId);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Contain("boom");
        row.SentUtc.Should().BeNull();
        // attempt_count=1, jitter pinned to 1.0, so delay = 2^0 * 1.0 = 1 second.
        row.NextAttemptAt.Should().Be(BaseTime.UtcDateTime.AddSeconds(1));
    }

    [Fact]
    public async Task Row_with_future_next_attempt_at_is_skipped()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher, time);
        var rowA = await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 1m));
        var rowB = await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 2m),
            nextAttemptAt: BaseTime.AddHours(1).UtcDateTime);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        dispatcher.Received.Should().HaveCount(1);
        dispatcher.Received[0].OutboxId.Should().Be(rowA);
        var stateB = await ReadRowAsync(dataSource, rowB);
        stateB.SentUtc.Should().BeNull();
        stateB.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Exceeds_max_attempts_quarantines_row_preserves_attempt_count()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher
        {
            Evaluate = (_, _) => Task.FromResult<Exception?>(new InvalidOperationException("boom")),
        };
        var processor = BuildProcessor(dataSource, dispatcher, time, maxAttempts: 3);
        var outboxId = await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 5m),
            attemptCount: 2);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        (await CountOutboxRowsAsync(dataSource)).Should().Be(0);
        var quarantined = await ReadQuarantineByOutboxIdAsync(dataSource, outboxId);
        quarantined.AttemptCount.Should().Be(3);
        quarantined.FinalError.Should().Contain("boom");
        quarantined.QuarantinedAt.Should().Be(BaseTime.UtcDateTime);
    }

    [Fact]
    public async Task Success_after_prior_failure_persists_sent_despite_attempt_count()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher, time);
        var outboxId = await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 1m),
            attemptCount: 5);

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(1);
        var row = await ReadRowAsync(dataSource, outboxId);
        row.AttemptCount.Should().Be(5);
        row.SentUtc.Should().Be(BaseTime.UtcDateTime);
    }

    [Fact]
    public async Task Dispatches_in_fifo_order_within_batch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher, time);
        var outboxIds = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var id = await SeedOutboxRowAsync(
                dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), i + 1m));
            outboxIds.Add(id);
        }

        var processed = await processor.ProcessBatchAsync(CancellationToken.None);

        processed.Should().Be(5);
        dispatcher.Received.Select(m => m.OutboxId).Should().Equal(outboxIds);
    }

    [Fact]
    public async Task Concurrent_processors_skip_locked_no_double_dispatch()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var time = new FakeTimeProvider(BaseTime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher1 = new RecordingDispatcher
        {
            Evaluate = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return null;
            },
        };
        var dispatcher2 = new RecordingDispatcher();
        var processor1 = BuildProcessor(dataSource, dispatcher1, time);
        var processor2 = BuildProcessor(dataSource, dispatcher2, time);
        await SeedOutboxRowAsync(
            dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 1m));

        // Processor 1 starts and holds the row lock inside its open transaction.
        var task1 = processor1.ProcessBatchAsync(CancellationToken.None);
        await entered.Task;

        // Processor 2's SELECT FOR UPDATE SKIP LOCKED skips the locked row and finds nothing.
        var count2 = await processor2.ProcessBatchAsync(CancellationToken.None);
        release.TrySetResult();
        var count1 = await task1;

        count1.Should().Be(1);
        count2.Should().Be(0);
        dispatcher1.Received.Should().HaveCount(1);
        dispatcher2.Received.Should().BeEmpty();
    }

    private static OutboxProcessor BuildProcessor(
        NpgsqlDataSource dataSource,
        IMessageDispatcher dispatcher,
        TimeProvider timeProvider,
        Func<double>? jitter = null,
        int batchSize = 100,
        int maxAttempts = 10)
    {
        var options = Options.Create(new OutboxProcessorOptions
        {
            BatchSize = batchSize,
            MaxAttempts = maxAttempts,
            TimeProvider = timeProvider,
            Jitter = jitter ?? (() => 1.0),
        });
        return new OutboxProcessor(
            new NpgsqlConnectionFactory(dataSource),
            dispatcher,
            CreateRegistry(),
            CreateJsonOptions(),
            new OutboxRetryPolicy(),
            options,
            NullLogger<OutboxProcessor>.Instance);
    }

    // global_position is NOT NULL on event_store.outbox as of migration 0002.
    // These tests seed outbox rows directly without a matching events row, so
    // the helper supplies the column itself; the value defaults to 1 because
    // most tests do not assert on it.
    private static async Task<long> SeedOutboxRowAsync(
        NpgsqlDataSource dataSource,
        Guid eventId,
        IDomainEvent payload,
        int attemptCount = 0,
        DateTime? nextAttemptAt = null,
        long globalPosition = 1)
    {
        var when = BaseTime.UtcDateTime;
        var json = CreateJsonOptions();
        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType(), json);
        var metadata = new EventMetadata(
            EventId: eventId,
            CorrelationId: Guid.NewGuid(),
            CausationId: Guid.NewGuid(),
            ActorId: Guid.Empty,
            Source: "test",
            SchemaVersion: 1,
            OccurredUtc: when);
        var metadataJson = JsonSerializer.Serialize(metadata, json);

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.outbox " +
            "(event_id, event_type, payload, metadata, occurred_utc, attempt_count, " +
            "next_attempt_at, global_position) " +
            "VALUES (@event_id, @event_type, @payload, @metadata, @occurred_utc, @attempt_count, " +
            "@next_attempt_at, @global_position) " +
            "RETURNING outbox_id";
        cmd.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, payload.GetType().Name);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadataJson);
        cmd.Parameters.AddWithValue("occurred_utc", NpgsqlDbType.TimestampTz, when);
        cmd.Parameters.AddWithValue("attempt_count", NpgsqlDbType.Integer, attemptCount);
        cmd.Parameters.AddWithValue("next_attempt_at", NpgsqlDbType.TimestampTz,
            (object?)nextAttemptAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("global_position", NpgsqlDbType.Bigint, globalPosition);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<OutboxRowState> ReadRowAsync(
        NpgsqlDataSource dataSource, long outboxId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT attempt_count, sent_utc, next_attempt_at, last_error " +
            "FROM event_store.outbox WHERE outbox_id = @outbox_id";
        cmd.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Outbox row {outboxId} not found");
        }
        return new OutboxRowState(
            AttemptCount: reader.GetInt32(0),
            SentUtc: reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
            NextAttemptAt: reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            LastError: reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<long> CountOutboxRowsAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event_store.outbox";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<QuarantineRowState> ReadQuarantineByOutboxIdAsync(
        NpgsqlDataSource dataSource, long outboxId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT attempt_count, final_error, quarantined_at " +
            "FROM event_store.outbox_quarantine WHERE outbox_id = @outbox_id";
        cmd.Parameters.AddWithValue("outbox_id", NpgsqlDbType.Bigint, outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Quarantine row for outbox_id {outboxId} not found");
        }
        return new QuarantineRowState(
            AttemptCount: reader.GetInt32(0),
            FinalError: reader.GetString(1),
            QuarantinedAt: DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
    }

    private readonly record struct OutboxRowState(
        int AttemptCount,
        DateTime? SentUtc,
        DateTime? NextAttemptAt,
        string? LastError);

    private readonly record struct QuarantineRowState(
        int AttemptCount,
        string FinalError,
        DateTime QuarantinedAt);
}
