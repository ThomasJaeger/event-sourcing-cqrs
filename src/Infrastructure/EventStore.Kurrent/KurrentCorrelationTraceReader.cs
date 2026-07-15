using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// The defense-in-depth ICorrelationTraceReader for a KurrentDB deployment. Correlation tracing reads the
// events by a correlation_id index that only the relational schema carries (a STORED generated column,
// migration 0001); KurrentDB has no equivalent, so the Correlation-ID Tracer is unavailable on this
// engine and the AdminConsole gates its page behind an unavailable-capability notice. This reader is the
// second line of that defense: it is registered so the port resolves and the host composes, and it
// throws the named reason if the page ever reaches it past the notice, rather than composing a silent
// wrong answer. It is a throwing implementation, not a placeholder.
public sealed class KurrentCorrelationTraceReader : ICorrelationTraceReader
{
    public const string UnavailableMessage =
        "Correlation tracing is not available on the KurrentDB event store: it carries no correlation-id "
        + "index. The Correlation-ID Tracer requires the PostgreSQL read side.";

    public Task<CorrelationTraceResult> ReadTraceAsync(
        Guid correlationId, int maxRows, CancellationToken ct)
        => throw new InvalidOperationException(UnavailableMessage);
}
