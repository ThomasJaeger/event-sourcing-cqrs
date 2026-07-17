using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.DynamoDb;

// The defense-in-depth ICorrelationTraceReader for a DynamoDB deployment, the twin of
// KurrentCorrelationTraceReader and unavailable for the same reason. Correlation tracing reads the
// events by a correlation_id index that only the relational schema carries (a STORED generated
// column, migration 0001). This table has two keys and no third: the stream id and the version, or
// the log partition and the global position (ADR 0049). Nothing indexes metadata, and the
// correlation id lives inside the metadata document, so answering "every event with this
// correlation id" would read the whole log and filter. That is a dedicated read path rather than a
// query, so the Correlation-ID Tracer is unavailable on this engine and the AdminConsole gates its
// page behind an unavailable-capability notice.
//
// This reader is the second line of that defense: it is registered so the port resolves and the host
// composes, and it throws the named reason if the page ever reaches it past the notice, rather than
// composing a silent wrong answer. It is a throwing implementation, not a placeholder.
public sealed class DynamoDbCorrelationTraceReader : ICorrelationTraceReader
{
    public const string UnavailableMessage =
        "Correlation tracing is not available on the DynamoDB event store: it carries no correlation-id "
        + "index. The Correlation-ID Tracer requires the PostgreSQL read side.";

    public Task<CorrelationTraceResult> ReadTraceAsync(
        Guid correlationId, int maxRows, CancellationToken ct)
        => throw new InvalidOperationException(UnavailableMessage);
}
