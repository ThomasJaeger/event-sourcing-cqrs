using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

// LISTEN/NOTIFY behaviour for OutboxProcessor: a wake on insert and a
// reconnect after the listener's backend session is terminated. Both tests
// run the processor's full StartAsync path (not the ProcessBatchAsync
// direct-call path the OutboxProcessorTests use), because the listener is
// what's under test. IdlePollInterval is raised to 30 seconds so any wake
// within the 2-second poll budget can only be the notification.
public class OutboxNotificationTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(2);

    private readonly PostgresFixture _fixture;

    public OutboxNotificationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Notification_wakes_processor_well_within_idle_poll_interval()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher);
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);
        try
        {
            // Insert via raw SQL so the trigger fires; the helper goes
            // through INSERT against event_store.outbox.
            var outboxId = await SeedOutboxRowAsync(
                dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 1m));

            var sentUtc = await PollForSentAsync(dataSource, outboxId, PollBudget);

            // The IdlePollInterval timer is set to 30 seconds; landing inside
            // the 2-second poll budget is only possible via LISTEN/NOTIFY.
            sentUtc.Should().NotBeNull(
                "LISTEN/NOTIFY should wake the processor within {0}, well under the {1} idle-poll timer",
                PollBudget, TimeSpan.FromSeconds(30));
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reconnect_after_listener_connection_drop_restores_wakeups()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var dispatcher = new RecordingDispatcher();
        var processor = BuildProcessor(dataSource, dispatcher);
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);
        try
        {
            // Let the listener settle into pg_stat_activity before
            // pg_terminate_backend looks for it.
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var terminatedCount = await TerminateListenerSessionsAsync(connStr);
            terminatedCount.Should().BeGreaterThan(
                0, "the listener session must be visible to pg_terminate_backend");

            // ListenerReconnectDelay is 1 second; allow 2.5 seconds for the
            // dispose-delay-reopen cycle to complete and the new LISTEN to
            // register before we trigger the next notification.
            await Task.Delay(TimeSpan.FromMilliseconds(2500));

            var outboxId = await SeedOutboxRowAsync(
                dataSource, Guid.NewGuid(), new TestPayload(Guid.NewGuid(), 2m));

            var sentUtc = await PollForSentAsync(dataSource, outboxId, PollBudget);

            sentUtc.Should().NotBeNull(
                "after a pg_terminate_backend on the listener session, the reconnect path should restore LISTEN/NOTIFY wakeups");
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private static OutboxProcessor BuildProcessor(
        NpgsqlDataSource dataSource, RecordingDispatcher dispatcher)
    {
        var options = Options.Create(new OutboxProcessorOptions
        {
            // 30s so any sub-second wake must be the notification, not the timer.
            IdlePollInterval = TimeSpan.FromSeconds(30),
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

    private static async Task<DateTime?> PollForSentAsync(
        NpgsqlDataSource dataSource, long outboxId, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var sentUtc = await ReadSentUtcAsync(dataSource, outboxId);
            if (sentUtc is not null)
            {
                return sentUtc;
            }
            await Task.Delay(PollInterval);
        }
        return await ReadSentUtcAsync(dataSource, outboxId);
    }

    private static async Task<DateTime?> ReadSentUtcAsync(
        NpgsqlDataSource dataSource, long outboxId)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sent_utc FROM event_store.outbox WHERE outbox_id = @id";
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Bigint, outboxId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull) return null;
        return DateTime.SpecifyKind((DateTime)result, DateTimeKind.Utc);
    }

    // pg_stat_activity.query records the connection's most-recent query, so
    // the listener's row carries "LISTEN \"outbox_pending\"" while it sits
    // parked in WaitAsync (state = 'idle'). The filter is loose because a
    // future Npgsql keepalive could wrap the bare LISTEN in another query
    // text; matching either form keeps the test honest if that lands.
    private static async Task<int> TerminateListenerSessionsAsync(string connStr)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT count(*) FROM (" +
            "  SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "  WHERE state = 'idle' " +
            "    AND (query LIKE 'LISTEN%' OR query LIKE '%LISTEN%')" +
            ") s";
        var result = await cmd.ExecuteScalarAsync();
        return (int)(long)result!;
    }

    private static async Task<long> SeedOutboxRowAsync(
        NpgsqlDataSource dataSource, Guid eventId, IDomainEvent payload)
    {
        var when = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
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
            "(event_id, event_type, payload, metadata, occurred_utc, global_position) " +
            "VALUES (@event_id, @event_type, @payload, @metadata, @occurred_utc, @global_position) " +
            "RETURNING outbox_id";
        cmd.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        cmd.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, payload.GetType().Name);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, metadataJson);
        cmd.Parameters.AddWithValue("occurred_utc", NpgsqlDbType.TimestampTz, when);
        cmd.Parameters.AddWithValue("global_position", NpgsqlDbType.Bigint, 1L);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
