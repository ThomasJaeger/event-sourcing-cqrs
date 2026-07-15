using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Infrastructure.EventStore.Kurrent;

// Duplicated from the relational adapters per ADR 0004. Same name, different namespace: each
// adapter raises its own type, and none references the other.
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
