namespace EventSourcingCqrs.Infrastructure.SignalR;

// Thrown when a NotificationEnvelope serializes to a payload that exceeds
// PostgreSQL's 8000-byte NOTIFY limit. The cap is a hard PostgreSQL constraint;
// pg_notify with an oversized payload would fail at COMMIT time, which is far
// from the publisher's call site and unrecoverable inside the unit-of-work's
// transaction. The publisher fails fast at the boundary with the actual byte
// count surfaced so an operator can correlate against the envelope shape.
public sealed class NotificationPayloadTooLargeException : Exception
{
    public NotificationPayloadTooLargeException(int actualByteCount, int maxByteCount)
        : base(
            $"NotificationEnvelope serializes to {actualByteCount} bytes, " +
            $"exceeding PostgreSQL's NOTIFY payload limit of {maxByteCount} bytes. " +
            "Reduce the envelope's content; the envelope is designed to carry " +
            "routing information only, not row data.")
    {
        ActualByteCount = actualByteCount;
        MaxByteCount = maxByteCount;
    }

    public int ActualByteCount { get; }
    public int MaxByteCount { get; }
}
