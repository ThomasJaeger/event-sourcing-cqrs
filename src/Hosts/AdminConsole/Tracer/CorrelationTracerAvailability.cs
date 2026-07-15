namespace EventSourcingCqrs.Hosts.AdminConsole.Tracer;

// Whether the Correlation-ID Tracer is available on the composed event store. It is available on the
// PostgreSQL read side, which carries the correlation_id index the tracer reads; it is unavailable on
// KurrentDB, which has no such index, and the reason rides along so the page can name it rather than
// failing at the operator's first trace. The /correlations page injects this and renders the
// reader-backed surface only when it is available, so the defense-in-depth throwing reader behind the
// unavailable arm is never reached in normal operation.
public sealed record CorrelationTracerAvailability(bool IsAvailable, string? UnavailableReason)
{
    public static CorrelationTracerAvailability Available { get; } = new(true, null);

    public static CorrelationTracerAvailability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new CorrelationTracerAvailability(false, reason);
    }
}
