namespace EventSourcingCqrs.Infrastructure.EventStore.Postgres;

// Thrown when CommandTypeRegistry cannot map a command type name to a CLR type,
// or a CLR type to a name. Parallel to UnknownEventTypeException but distinct so
// the message and the catch site say "command": a stored command type that no
// longer resolves is a command-registration problem, and an exception that said
// "event" would mislead the reader chasing it.
public sealed class UnknownCommandTypeException : Exception
{
    public string TypeName { get; }

    public UnknownCommandTypeException(string typeName)
        : base($"No CLR type is registered for command type name '{typeName}'.")
    {
        TypeName = typeName;
    }
}
