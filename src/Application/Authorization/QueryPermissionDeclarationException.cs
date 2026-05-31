namespace EventSourcingCqrs.Application.Authorization;

// Thrown at composition when the structural check finds a concrete Application query that declares no
// required permission. A loud startup failure, the read-side twin of
// CommandPermissionDeclarationException: a query that reached production without a permission
// declaration would run unauthorized, so the gap is closed before the host serves a request.
public sealed class QueryPermissionDeclarationException : Exception
{
    public IReadOnlyCollection<Type> UndeclaredQueries { get; }

    public QueryPermissionDeclarationException(IReadOnlyCollection<Type> undeclaredQueries)
        : base("These query types declare no required permission and must implement " +
               "IAuthorizedQuery: " +
               string.Join(", ", undeclaredQueries.Select(t => t.Name)) + ".")
    {
        UndeclaredQueries = undeclaredQueries;
    }
}
