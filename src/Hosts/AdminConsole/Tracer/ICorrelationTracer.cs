namespace EventSourcingCqrs.Hosts.AdminConsole.Tracer;

// The Correlation-ID Tracer's read seam (Chapter 17): it traces one workflow across every stream and every
// tenant that carries its correlation id. It owns the parse guard (input that is not a Guid returns
// InvalidFormat), the sentinel guard (Guid.Empty parses but is not a workflow, so it returns
// SentinelRefused), the cap it hands the port, and the per-row family classification the page renders.
// Authorization is the host fallback gate (ADR 0040), upstream of the page that calls this.
//
// There is no ProcessManagerUnsupported analogue to the Browser's, by decision. Process-manager rows are in
// scope: the read resolves no payload type, so one query returns both event families and the seam has
// nothing to refuse (ADR 0043).
//
// A metadata deserialize that fails surfaces through this seam uncaught. Metadata is the platform's own
// fixed shape, so a row that cannot produce it is a defect the operator needs to see rather than a row to
// render half of, and the page owns the error state (ADR 0043).
public interface ICorrelationTracer
{
    Task<CorrelationTraceView> TraceCorrelationAsync(string correlationIdInput, CancellationToken ct);
}
