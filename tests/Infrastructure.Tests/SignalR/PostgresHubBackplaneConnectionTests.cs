using System.Diagnostics;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SignalR;

public class PostgresHubBackplaneConnectionTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresHubBackplaneConnectionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubscribeAsync_yields_envelope_when_pg_notify_fires()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var backplane = BuildBackplane(connStr);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var collectTask = Task.Run(async () =>
        {
            await foreach (var envelope in backplane.SubscribeAsync(cts.Token))
            {
                return envelope;
            }
            return null;
        }, cts.Token);

        // pg_notify does not queue for absent listeners, so the publish below is lost
        // unless the listener has attached. Wait on the observation, not a duration.
        await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName, ct: cts.Token);

        var sent = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-yield",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);

        await PublishNotificationAsync(connStr, sent);

        var received = await collectTask;
        received.Should().NotBeNull();
        received.Should().BeEquivalentTo(sent);
    }

    [Fact]
    public async Task SubscribeAsync_preserves_envelope_shape_through_deserialization()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var backplane = BuildBackplane(connStr);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var collectTask = Task.Run(async () =>
        {
            await foreach (var envelope in backplane.SubscribeAsync(cts.Token))
            {
                return envelope;
            }
            return null;
        }, cts.Token);

        await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName, ct: cts.Token);

        var sent = new NotificationEnvelope(
            ProjectionName: "InventoryDashboard",
            ResourceId: "sku-abc",
            EventName: "InventoryAdjusted",
            Widgets: new[] { "on_hand", "reserved", "available" },
            Tenant: WellKnownTenants.Default);

        await PublishNotificationAsync(connStr, sent);

        var received = await collectTask;
        received!.ProjectionName.Should().Be("InventoryDashboard");
        received.ResourceId.Should().Be("sku-abc");
        received.EventName.Should().Be("InventoryAdjusted");
        received.Widgets.Should().Equal("on_hand", "reserved", "available");
    }

    [Fact]
    public async Task SubscribeAsync_stops_iteration_on_cancellation()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var backplane = BuildBackplane(connStr);

        using var cts = new CancellationTokenSource();

        var iterationTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in backplane.SubscribeAsync(cts.Token))
                {
                    // Never reached: no publishes happen before cancellation.
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected when iteration completes via cancellation.
            }
        });

        // Let the listener attach, then cancel. Cancelling before the attach would
        // still end the iteration, so the fact would pass without covering the case
        // it names: a listener already parked when cancellation arrives.
        await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName);
        await cts.CancelAsync();

        // Iteration should complete cleanly without hanging.
        var winner = await Task.WhenAny(iterationTask, Task.Delay(5000));
        winner.Should().BeSameAs(iterationTask);
    }

    [Fact]
    public async Task Malformed_payload_is_logged_and_skipped_listener_continues()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var recordingLogger = new RecordingLogger<PostgresHubBackplaneConnection>();
        await using var backplane = BuildBackplane(connStr, logger: recordingLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receivedEnvelopes = new List<NotificationEnvelope>();
        var collectTask = Task.Run(async () =>
        {
            await foreach (var envelope in backplane.SubscribeAsync(cts.Token))
            {
                receivedEnvelopes.Add(envelope);
                if (receivedEnvelopes.Count >= 1) break;
            }
        }, cts.Token);

        // The malformed payload below is the one this fact is about, and it is the one
        // a late attach loses: the valid payload still arrives, the count assertion
        // still passes, and only the logger assertion notices. Wait on the attach.
        await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName, ct: cts.Token);

        // Send a malformed payload first, then a valid one. The malformed
        // payload is logged and skipped; the valid payload arrives normally.
        await PublishRawAsync(connStr, "{ this is not valid json");
        await Task.Delay(200, cts.Token);

        var valid = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-valid",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);
        await PublishNotificationAsync(connStr, valid);

        await collectTask;

        receivedEnvelopes.Should().HaveCount(1);
        receivedEnvelopes[0].Should().BeEquivalentTo(valid);
        recordingLogger.LoggedEntries.Should().Contain(e =>
            e.Exception is NotificationDeserializationException);
    }

    [Fact]
    public async Task DisposeAsync_disposes_data_source_cleanly()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var backplane = BuildBackplane(connStr);

        var act = async () => await backplane.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Listener_reconnects_after_underlying_connection_drops()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var backplane = BuildBackplane(
            connStr, reconnectDelay: TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var receivedEnvelopes = new List<NotificationEnvelope>();
        var collectTask = Task.Run(async () =>
        {
            await foreach (var envelope in backplane.SubscribeAsync(cts.Token))
            {
                receivedEnvelopes.Add(envelope);
                if (receivedEnvelopes.Count >= 1) break;
            }
        }, cts.Token);

        // Observe the listener's backend before terminating it. Its pid is what makes
        // the reconnect observable further down: the replacement session gets a new
        // pid, so excluding this one distinguishes a listener that came back from a
        // listener that never left.
        var originalPids = await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName, ct: cts.Token);

        // Terminate that backend by pid, and assert it died. Without the count
        // this fact passes vacuously when the terminate misses: the original listener
        // survives, the publish below reaches it, and the reconnect path this fact
        // exists to cover never runs.
        var terminated = await PostgresListenerProbe.TerminateAsync(connStr, originalPids);
        terminated.Should().Be(
            originalPids.Count,
            "the listener backend must be terminated for the reconnect path to run");

        // Wait for a listener that is not the one just killed.
        await PostgresListenerProbe.WaitForListenersAsync(
            connStr,
            NotificationContract.ChannelName,
            excluding: originalPids,
            ct: cts.Token);

        // Publish through a fresh connection; the reconnected listener should
        // observe the notification.
        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-reconnect",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);
        await PublishNotificationAsync(connStr, envelope);

        await collectTask;
        receivedEnvelopes.Should().HaveCount(1);
        receivedEnvelopes[0].Should().BeEquivalentTo(envelope);
    }

    // A consumer that stops iterating before its token is cancelled has its enumerator disposed
    // promptly. The listener parks until cancelled, so the iterator's finally cancels the token it
    // owns before awaiting the listener. Awaiting first is what used to block: the listener ran on
    // the caller's token, and an early break waited out the caller's whole budget.
    //
    // The two budgets sit far apart on purpose. The token gets 30 seconds and the disposal gets
    // 2, a 15:1 ratio, so a pass cannot be manufactured by the token firing inside the disposal
    // wait: at the moment the verdict is taken, 28 of the token's 30 seconds are still unspent.
    [Fact]
    public async Task Disposing_the_enumerator_after_an_early_break_does_not_wait_for_cancellation()
    {
        var tokenBudget = TimeSpan.FromSeconds(30);
        var disposalBudget = TimeSpan.FromSeconds(2);

        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var backplane = BuildBackplane(connStr);

        using var cts = new CancellationTokenSource(tokenBudget);

        // GetAsyncEnumerator(default) is what await-foreach emits; the token rides the method
        // argument. The iterator body does not run, and no LISTEN is issued, until the first
        // MoveNextAsync, so that call starts before the attach probe and is awaited after it.
        var enumerator = backplane.SubscribeAsync(cts.Token).GetAsyncEnumerator(default);
        var firstMove = enumerator.MoveNextAsync().AsTask();

        await PostgresListenerProbe.WaitForListenersAsync(
            connStr, NotificationContract.ChannelName, ct: cts.Token);

        var sent = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-early-break",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);
        await PublishNotificationAsync(connStr, sent);

        // A real envelope arrives through the real notification path before the break, so this
        // covers a consumer that stops partway rather than one that never started.
        (await firstMove).Should().BeTrue("the subscription must yield before a break means anything");
        var received = enumerator.Current;
        received.Should().BeEquivalentTo(sent);

        // Disposing the enumerator is what breaking out of an await-foreach does.
        var elapsed = Stopwatch.StartNew();
        var disposal = enumerator.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(disposal, Task.Delay(disposalBudget, CancellationToken.None));
        elapsed.Stop();
        var disposedInTime = ReferenceEquals(winner, disposal);

        // Read for the failure message, so a regression says which await is blocking rather than
        // reporting a bare timeout. A listener that exited disposed its connection in its own
        // finally and leaves no backend; a backend still parked on the channel is a listener the
        // teardown failed to release. Empty is the passing shape.
        var pidsAfterBudget = await PostgresListenerProbe.ListeningPidsAsync(
            connStr, NotificationContract.ChannelName);

        disposedInTime.Should().BeTrue(
            "an early break must dispose the enumerator within {0}, but it was still blocked after "
            + "that budget with the consumer's token uncancelled ({1} of {2} unspent) and the "
            + "listener backend still parked on the channel (pids [{3}])",
            disposalBudget,
            tokenBudget - elapsed.Elapsed,
            tokenBudget,
            string.Join(",", pidsAfterBudget));
    }

    private static PostgresHubBackplaneConnection BuildBackplane(
        string connStr,
        ILogger<PostgresHubBackplaneConnection>? logger = null,
        TimeSpan? reconnectDelay = null)
    {
        var options = Options.Create(new HubBackplaneOptions
        {
            ConnectionString = connStr,
            ChannelName = NotificationContract.ChannelName,
            ReconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1),
        });
        return new PostgresHubBackplaneConnection(
            options,
            logger ?? NullLogger<PostgresHubBackplaneConnection>.Instance);
    }

    private static async Task PublishNotificationAsync(string connStr, NotificationEnvelope envelope)
    {
        var payloadJson = JsonSerializer.Serialize(envelope, NotificationContract.SerializerOptions);
        await PublishRawAsync(connStr, payloadJson);
    }

    private static async Task PublishRawAsync(string connStr, string payload)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_notify(@channel, @payload)";
        cmd.Parameters.AddWithValue(
            "channel", NpgsqlDbType.Text, NotificationContract.ChannelName);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Text, payload);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> LoggedEntries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedEntries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
