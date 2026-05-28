using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using EventSourcingCqrs.Application.SignalR;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventSourcingCqrs.Infrastructure.SignalR;

// PostgreSQL LISTEN-side of the projection_committed channel. Holds a dedicated
// long-lived NpgsqlConnection parked on WaitAsync; on notification, deserializes
// the payload as a NotificationEnvelope and yields it through SubscribeAsync's
// IAsyncEnumerable. ADR 0027.
//
// The listener-side lifecycle parallels OutboxProcessor: dedicated connection,
// reconnect-on-drop with a configurable delay, identifier-quoted LISTEN. The
// key difference is the queueing: OutboxProcessor's notification is a wake
// signal for a table-driven consumer, so a TaskCompletionSource is sufficient.
// The backplane's notifications carry data directly, so concurrent arrivals
// must not be lost. The implementation uses an unbounded
// System.Threading.Channels.Channel to buffer envelopes between the listener
// thread and the consumer; the unbounded shape is honest for the v1 expected
// rate (a few notifications per second across all projections), with bounded
// backpressure available as a future-scaling refinement.
//
// Connection sourcing is self-contained: the adapter constructs its own
// NpgsqlDataSource from the configured connection string and disposes it when
// the adapter disposes. This keeps the Hosts.Web composition graph narrow:
// Hosts.Web sees IHubBackplaneConnection, gets one Postgres LISTEN connection,
// inherits no event-store or read-model machinery.
public sealed class PostgresHubBackplaneConnection : IHubBackplaneConnection, IAsyncDisposable
{
    private readonly HubBackplaneOptions _options;
    private readonly ILogger<PostgresHubBackplaneConnection> _logger;
    private readonly NpgsqlDataSource _dataSource;

    public PostgresHubBackplaneConnection(
        IOptions<HubBackplaneOptions> options,
        ILogger<PostgresHubBackplaneConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        ArgumentException.ThrowIfNullOrEmpty(
            _options.ConnectionString,
            $"{nameof(HubBackplaneOptions)}.{nameof(HubBackplaneOptions.ConnectionString)}");
        ArgumentException.ThrowIfNullOrEmpty(
            _options.ChannelName,
            $"{nameof(HubBackplaneOptions)}.{nameof(HubBackplaneOptions.ChannelName)}");
        _dataSource = NpgsqlDataSource.Create(_options.ConnectionString);
    }

    // Yields NotificationEnvelopes as they arrive on the LISTEN channel.
    // Reconnects internally on connection drops; logs malformed payloads and
    // continues. Cancellation cleanly ends the iteration; dispose cleanly
    // releases the connection.
    public async IAsyncEnumerable<NotificationEnvelope> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<NotificationEnvelope>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var listenerTask = Task.Run(() => RunListenerAsync(channel.Writer, ct), ct);

        try
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(ct))
            {
                yield return envelope;
            }
        }
        finally
        {
            // Listener task's finally block also completes the writer; this is a
            // belt-and-suspenders to handle the case where the consumer stops
            // iterating before the listener exits.
            channel.Writer.TryComplete();
            try { await listenerTask; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
    }

    private async Task RunListenerAsync(
        ChannelWriter<NotificationEnvelope> writer,
        CancellationToken ct)
    {
        NpgsqlConnection? connection = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    connection = await _dataSource.OpenConnectionAsync(ct);
                    connection.Notification += (_, args) => OnNotification(writer, args);
                    await using (var listenCmd = connection.CreateCommand())
                    {
                        listenCmd.CommandText =
                            $"LISTEN {QuoteIdentifier(_options.ChannelName)}";
                        await listenCmd.ExecuteNonQueryAsync(ct);
                    }

                    while (!ct.IsCancellationRequested)
                    {
                        await connection.WaitAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Backplane listener connection dropped; reconnecting in {Delay}",
                        _options.ReconnectDelay);
                    if (connection is not null)
                    {
                        try { await connection.DisposeAsync(); }
                        catch (Exception disposeEx)
                        {
                            _logger.LogWarning(
                                disposeEx,
                                "Backplane listener dispose failed during reconnect");
                        }
                        connection = null;
                    }
                    try
                    {
                        await Task.Delay(_options.ReconnectDelay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex, "Backplane listener dispose failed on shutdown");
                }
            }
            writer.TryComplete();
        }
    }

    private void OnNotification(
        ChannelWriter<NotificationEnvelope> writer,
        NpgsqlNotificationEventArgs args)
    {
        NotificationEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<NotificationEnvelope>(
                args.Payload, NotificationContract.SerializerOptions);
            if (envelope is null)
            {
                _logger.LogError(
                    new NotificationDeserializationException(
                        args.Payload, new JsonException("Deserialized envelope was null.")),
                    "Backplane notification deserialized to null. Payload={Payload}",
                    args.Payload);
                return;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                new NotificationDeserializationException(args.Payload, ex),
                "Backplane notification deserialization failed. Payload={Payload}",
                args.Payload);
            return;
        }

        if (!writer.TryWrite(envelope))
        {
            _logger.LogWarning(
                "Backplane channel rejected envelope; consumer may have completed. " +
                "ProjectionName={ProjectionName} ResourceId={ResourceId}",
                envelope.ProjectionName, envelope.ResourceId);
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }
}
