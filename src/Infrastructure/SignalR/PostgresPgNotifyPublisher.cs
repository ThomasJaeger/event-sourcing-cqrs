using System.Text;
using System.Text.Json;
using EventSourcingCqrs.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Infrastructure.SignalR;

// Publishes NotificationEnvelopes via PostgreSQL pg_notify on the
// projection_committed channel, inside the caller's existing transaction.
// pg_notify is transactional: notifications are delivered to listeners at
// COMMIT time, and rollback suppresses delivery. The SignalR hub at Hosts.Web
// LISTENs on the channel and broadcasts received envelopes to per-resource
// groups. ADR 0027.
//
// This adapter is consumed by projection unit-of-works through its concrete
// type, not through the INotificationPublisher port. The unit-of-work owns
// the NpgsqlTransaction and calls PublishOnTransactionAsync directly. The port
// surface lives in Domain.Abstractions to document the abstract contract for
// future adapters; the port method itself has no honest implementation against
// the Postgres carrier (the carrier requires a transaction the port does not
// expose), so it throws InvalidOperationException naming the design
// constraint. A future event-store adapter whose carrier is non-transactional
// (or transactional through different machinery) implements the port method
// directly.
public sealed class PostgresPgNotifyPublisher : INotificationPublisher
{
    // PostgreSQL's pg_notify imposes an 8000-byte limit on the payload string
    // (NAMEDATALEN-derived; documented in the PostgreSQL LISTEN documentation).
    // Envelopes serialize to 100-300 bytes typically; the cap exists to fail
    // fast on a future change that puts row data in the envelope.
    public const int MaxPayloadBytes = 8000;

    public const string ChannelName = "projection_committed";

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<PostgresPgNotifyPublisher> _logger;

    public PostgresPgNotifyPublisher(
        JsonSerializerOptions jsonOptions,
        ILogger<PostgresPgNotifyPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(jsonOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _jsonOptions = jsonOptions;
        _logger = logger;
    }

    // The port method has no honest implementation against the Postgres
    // carrier: pg_notify must ride a caller-owned transaction, and the port
    // does not expose one. A projection unit-of-work invokes
    // PublishOnTransactionAsync below; this method exists to satisfy the
    // INotificationPublisher contract and fails loudly if reached.
    public Task PublishAsync(NotificationEnvelope envelope, CancellationToken ct)
        => throw new InvalidOperationException(
            "PostgresPgNotifyPublisher requires a caller-owned NpgsqlTransaction " +
            "to publish transactionally with the projection's commit. " +
            "Use PublishOnTransactionAsync from within a unit-of-work's CommitAsync.");

    // Issues NOTIFY projection_committed on the supplied transaction. The
    // notification is queued by PostgreSQL and delivered to LISTEN subscribers
    // at COMMIT time; rollback suppresses delivery. The payload is the
    // envelope's JSON serialization; oversized payloads throw
    // NotificationPayloadTooLargeException at the boundary, before the NOTIFY
    // is issued, so the failure surfaces at the publisher's call site rather
    // than at COMMIT time deep inside the unit-of-work.
    public async Task PublishOnTransactionAsync(
        NotificationEnvelope envelope,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(transaction);

        var payloadJson = JsonSerializer.Serialize(envelope, _jsonOptions);
        var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > MaxPayloadBytes)
        {
            throw new NotificationPayloadTooLargeException(payloadBytes, MaxPayloadBytes);
        }

        // pg_notify(channel, payload) accepts parameters; NOTIFY (the SQL
        // statement) does not. The function form keeps the call parameterized
        // and avoids hand-rolled escaping of the payload's JSON, which can
        // legitimately contain single quotes.
        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT pg_notify(@channel, @payload)";
        cmd.Parameters.AddWithValue("channel", NpgsqlDbType.Text, ChannelName);
        cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Text, payloadJson);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation(
            "Published projection notification. " +
            "ProjectionName={ProjectionName} ResourceId={ResourceId} " +
            "EventName={EventName} PayloadBytes={PayloadBytes}",
            envelope.ProjectionName, envelope.ResourceId,
            envelope.EventName, payloadBytes);
    }
}
