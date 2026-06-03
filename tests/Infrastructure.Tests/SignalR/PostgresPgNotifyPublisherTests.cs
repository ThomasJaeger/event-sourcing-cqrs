using System.Text;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Infrastructure.SignalR;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Infrastructure.Tests.SignalR;

public class PostgresPgNotifyPublisherTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresPgNotifyPublisherTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PublishOnTransactionAsync_emits_notification_to_LISTEN_subscriber_on_commit()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var publisher = BuildPublisher();

        // The LISTEN subscriber holds a dedicated connection parked on
        // WaitAsync, mirroring the production hub backplane's shape (ADR 0027,
        // OutboxProcessor precedent).
        await using var listenerConnection = new NpgsqlConnection(connStr);
        await listenerConnection.OpenAsync();
        var receivedPayloads = new List<string>();
        listenerConnection.Notification += (_, args) => receivedPayloads.Add(args.Payload);
        await using (var listenCmd = listenerConnection.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {NotificationContract.ChannelName}";
            await listenCmd.ExecuteNonQueryAsync();
        }

        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-abc",
            EventName: "OrderShipped",
            Widgets: new[] { "status", "timeline" },
            Tenant: WellKnownTenants.Default);

        await using (var publisherConnection = new NpgsqlConnection(connStr))
        {
            await publisherConnection.OpenAsync();
            await using var transaction = await publisherConnection.BeginTransactionAsync();
            await publisher.PublishOnTransactionAsync(envelope, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        // pg_notify delivers at COMMIT time; WaitAsync with a short timeout
        // drains the notification queue. The token-based timeout fails the test
        // fast if delivery doesn't happen rather than hanging indefinitely.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await listenerConnection.WaitAsync(cts.Token);

        receivedPayloads.Should().HaveCount(1);
        var deserialized = JsonSerializer.Deserialize<NotificationEnvelope>(receivedPayloads[0], NotificationContract.SerializerOptions);
        deserialized.Should().BeEquivalentTo(envelope);
    }

    [Fact]
    public async Task PublishOnTransactionAsync_suppresses_notification_on_rollback()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var publisher = BuildPublisher();

        await using var listenerConnection = new NpgsqlConnection(connStr);
        await listenerConnection.OpenAsync();
        var receivedPayloads = new List<string>();
        listenerConnection.Notification += (_, args) => receivedPayloads.Add(args.Payload);
        await using (var listenCmd = listenerConnection.CreateCommand())
        {
            listenCmd.CommandText = $"LISTEN {NotificationContract.ChannelName}";
            await listenCmd.ExecuteNonQueryAsync();
        }

        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-xyz",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);

        await using (var publisherConnection = new NpgsqlConnection(connStr))
        {
            await publisherConnection.OpenAsync();
            await using var transaction = await publisherConnection.BeginTransactionAsync();
            await publisher.PublishOnTransactionAsync(envelope, transaction, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        // No COMMIT means pg_notify never delivers. Give the listener a short
        // window to confirm nothing arrives; the cancellation expiring is the
        // pass signal.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await listenerConnection.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: no notification arrived within the window.
        }

        receivedPayloads.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishOnTransactionAsync_throws_NotificationPayloadTooLargeException_when_payload_exceeds_cap()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var publisher = BuildPublisher();

        // Build an envelope whose serialization exceeds the 8000-byte cap.
        // A widgets collection of 1000 entries each holding 16 characters
        // serializes to well over the cap once JSON quotes and commas are
        // included.
        var oversizedWidgets = Enumerable
            .Range(0, 1000)
            .Select(i => $"widget_{i:D8}")
            .ToArray();
        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-too-big",
            EventName: "OrderShipped",
            Widgets: oversizedWidgets,
            Tenant: WellKnownTenants.Default);

        await using var publisherConnection = new NpgsqlConnection(connStr);
        await publisherConnection.OpenAsync();
        await using var transaction = await publisherConnection.BeginTransactionAsync();

        var act = async () => await publisher.PublishOnTransactionAsync(
            envelope, transaction, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<NotificationPayloadTooLargeException>();
        ex.Which.MaxByteCount.Should().Be(PostgresPgNotifyPublisher.MaxPayloadBytes);
        ex.Which.ActualByteCount.Should().BeGreaterThan(PostgresPgNotifyPublisher.MaxPayloadBytes);
    }

    [Fact]
    public async Task PublishAsync_port_method_throws_InvalidOperationException_naming_the_transaction_requirement()
    {
        var publisher = BuildPublisher();
        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-no-tx",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);

        var act = async () => await publisher.PublishAsync(envelope, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a caller-owned NpgsqlTransaction*PublishOnTransactionAsync*");
    }

    [Fact]
    public async Task PublishOnTransactionAsync_propagates_cancellation()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var publisher = BuildPublisher();
        var envelope = new NotificationEnvelope(
            ProjectionName: "OrderDetail",
            ResourceId: "order-cancel",
            EventName: "OrderShipped",
            Widgets: new[] { "status" },
            Tenant: WellKnownTenants.Default);

        await using var publisherConnection = new NpgsqlConnection(connStr);
        await publisherConnection.OpenAsync();
        await using var transaction = await publisherConnection.BeginTransactionAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await publisher.PublishOnTransactionAsync(
            envelope, transaction, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static PostgresPgNotifyPublisher BuildPublisher()
        => new(NullLogger<PostgresPgNotifyPublisher>.Instance);
}
