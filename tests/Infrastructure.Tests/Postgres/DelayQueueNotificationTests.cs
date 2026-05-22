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

// LISTEN/NOTIFY wake for DelayQueueProcessor, mirroring OutboxNotificationTests'
// wake test. The reconnect test is not duplicated here: the listener machinery
// is a verbatim copy of OutboxProcessor's and is already covered by
// OutboxNotificationTests. IdlePollInterval is 30s so any wake within the
// 2-second budget can only be the notification, not the timer.
public class DelayQueueNotificationTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(2);
    private static readonly SystemActor Pm = SystemActors.OrderFulfillment;

    private readonly PostgresFixture _fixture;

    public DelayQueueNotificationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Notification_wakes_processor_well_within_idle_poll_interval()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var bus = new SignallingCausedCommandBus();
        var processor = BuildProcessor(dataSource, bus);
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);
        try
        {
            // Insert a due row via raw SQL so the AFTER INSERT trigger fires.
            await SeedDueRowAsync(dataSource);

            var completed = await Task.WhenAny(bus.Dispatched, Task.Delay(PollBudget));

            completed.Should().BeSameAs(
                bus.Dispatched,
                "LISTEN/NOTIFY should wake the processor within {0}, well under the 30s idle-poll timer",
                PollBudget);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }
    }

    private static DelayQueueProcessor BuildProcessor(
        NpgsqlDataSource dataSource, SignallingCausedCommandBus bus)
    {
        var options = Options.Create(new DelayQueueProcessorOptions
        {
            // 30s so any sub-second wake must be the notification, not the timer.
            IdlePollInterval = TimeSpan.FromSeconds(30),
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

    private static async Task SeedDueRowAsync(NpgsqlDataSource dataSource)
    {
        var payloadJson = JsonSerializer.Serialize(
            new TestTimeout(Guid.NewGuid()), typeof(TestTimeout), CreateJsonOptions());

        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO event_store.delayed_commands " +
            "(fire_at_utc, command_type, command_payload, correlation_id, causation_id, " +
            "actor_id, service_name, idempotency_key, scheduled_by_stream_id, scheduled_by_step) " +
            "VALUES (now(), @type, @payload, @correlation, @causation, @actor, @service, " +
            "@key, @stream, @step)";
        cmd.Parameters.AddWithValue("type", NpgsqlDbType.Text, nameof(TestTimeout));
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("correlation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("causation", NpgsqlDbType.Uuid, Guid.NewGuid());
        cmd.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, Pm.Id);
        cmd.Parameters.AddWithValue("service", NpgsqlDbType.Text, Pm.ServiceName);
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, "key-notify");
        cmd.Parameters.AddWithValue("stream", NpgsqlDbType.Text, $"pm-order-fulfillment:{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("step", NpgsqlDbType.Text, "await-payment");
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record TestTimeout(Guid OrderId) : ICommand;

    private sealed class SignallingCausedCommandBus : ICausedCommandBus
    {
        private readonly TaskCompletionSource _dispatched =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Dispatched => _dispatched.Task;

        public Task SendAsync(
            ICommand command,
            EventMetadata causingEventMetadata,
            SystemActor actor,
            string? idempotencyKey,
            CancellationToken ct)
        {
            _dispatched.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
