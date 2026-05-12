namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

public sealed class UnknownEventTypeException : Exception
{
    public string TypeName { get; }
    public Guid? StreamId { get; }

    public UnknownEventTypeException(string typeName)
        : base($"No CLR type is registered for event type name '{typeName}'.")
    {
        TypeName = typeName;
    }

    public UnknownEventTypeException(string typeName, Guid streamId, Exception? innerException = null)
        : base(
            $"No CLR type is registered for event type name '{typeName}' while reading stream {streamId}.",
            innerException)
    {
        TypeName = typeName;
        StreamId = streamId;
    }
}
