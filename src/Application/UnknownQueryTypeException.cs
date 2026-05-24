namespace EventSourcingCqrs.Application;

// Thrown when QueryTypeRegistry cannot map a query type name to a CLR type, or a
// CLR type to a name. Parallel to UnknownCommandTypeException but distinct so the
// message and the catch site say "query": an envelope token that no longer
// resolves is a query-registration problem, and an exception that said "command"
// or "event" would mislead the reader chasing it.
public sealed class UnknownQueryTypeException : Exception
{
    public string TypeName { get; }

    public UnknownQueryTypeException(string typeName)
        : base($"No CLR type is registered for query type name '{typeName}'.")
    {
        TypeName = typeName;
    }
}
