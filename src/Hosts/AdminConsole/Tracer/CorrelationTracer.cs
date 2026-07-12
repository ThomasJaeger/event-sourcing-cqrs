using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tracer;

// The single implementation of ICorrelationTracer. It reads one workflow's rows through
// ICorrelationTraceReader and shapes them for operator display. The checks run in a fixed precedence, each
// guarding the next: parse first, so an input that is not a Guid never reaches the port; then the sentinel
// guard, so Guid.Empty never runs a query that post-ADR 0042 matches only the process-manager rows written
// before that fix; then the read.
//
// The port's throw on a metadata deserialize passes through uncaught, by decision (ADR 0043). Metadata is
// the platform's own fixed shape, so a row that cannot produce it is a defect the operator needs to see,
// and the page renders its error state.
public sealed class CorrelationTracer : ICorrelationTracer
{
    // Bounds an operator-facing read of a table nothing else bounds, and is deliberately not operator
    // input: a trace that runs past it is a question for a query tool rather than a console page.
    public const int DefaultMaxRows = 500;

    private readonly ICorrelationTraceReader _reader;
    private readonly int _maxRows;

    public CorrelationTracer(ICorrelationTraceReader reader, int maxRows = DefaultMaxRows)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        _reader = reader;
        _maxRows = maxRows;
    }

    public async Task<CorrelationTraceView> TraceCorrelationAsync(
        string correlationIdInput,
        CancellationToken ct)
    {
        if (!Guid.TryParse(correlationIdInput, out var correlationId))
        {
            return Refused(CorrelationTraceOutcome.InvalidFormat);
        }

        if (correlationId == Guid.Empty)
        {
            return Refused(CorrelationTraceOutcome.SentinelRefused);
        }

        var trace = await _reader.ReadTraceAsync(correlationId, _maxRows, ct);
        if (trace.Rows.Count == 0)
        {
            return Refused(CorrelationTraceOutcome.Empty);
        }

        var events = trace.Rows.Select(ToTracedEvent).ToList();
        return new CorrelationTraceView(CorrelationTraceOutcome.Found, events, trace.Truncated);
    }

    private static CorrelationTraceView Refused(CorrelationTraceOutcome outcome)
        => new(outcome, [], Truncated: false);

    private static TracedEvent ToTracedEvent(CorrelationTraceRow row)
        => new(
            StreamId: row.StreamId,
            Kind: KindOf(row.StreamId),
            StreamVersion: row.StreamVersion,
            EventType: row.EventType,
            EventVersion: row.EventVersion,
            OccurredUtc: row.OccurredUtc,
            GlobalPosition: row.GlobalPosition,
            PayloadJson: row.PayloadJson,
            Metadata: row.Metadata);

    // The stream-id prefix is the only per-row discriminator a trace has, and that is by design: the read
    // resolves no payload type (ADR 0043), so nothing else on the row says which family it belongs to. Same
    // ordinal prefix test the Browser's pre-detection and the store's PM read guards run, through the shared
    // constant.
    private static TracedEventKind KindOf(string streamId)
        => streamId.StartsWith(StreamPrefixes.ProcessManagerPrefix, StringComparison.Ordinal)
            ? TracedEventKind.ProcessManager
            : TracedEventKind.Aggregate;
}
