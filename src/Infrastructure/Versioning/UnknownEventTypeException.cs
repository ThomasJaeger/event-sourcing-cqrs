using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.Versioning;

// Raised when a storage-side event type name has no registered CLR type, or a CLR type has
// no registered name. One type across every engine now, consolidated from the three adapters
// per ADR 0048. Before the consolidation each adapter raised its own same-named type from its
// own namespace, so a caller catching one engine's exception silently missed another's. The
// failure it reports is a registry gap, which is a versioning fault and identical on every
// engine.
public sealed class UnknownEventTypeException : Exception
{
    public string TypeName { get; }
    public StreamId? StreamId { get; }

    public UnknownEventTypeException(string typeName)
        : base($"No CLR type is registered for event type name '{typeName}'.")
    {
        TypeName = typeName;
    }

    public UnknownEventTypeException(
        string typeName, StreamId streamId, Exception? innerException = null)
        : base(
            $"No CLR type is registered for event type name '{typeName}' while reading stream {streamId}.",
            innerException)
    {
        TypeName = typeName;
        StreamId = streamId;
    }
}
