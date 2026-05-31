using EventSourcingCqrs.Domain.Abstractions;

namespace EventSourcingCqrs.Application.Authorization;

// The composition-time structural check: every concrete query in the Application assembly must declare
// a required permission by implementing IAuthorizedQuery. The read-side twin of
// CommandPermissionValidation, factored from the registration site so the failure path is provable in a
// unit test without planting a non-conforming query in the assembly the real walk scans. IQuery<TResult>
// has no non-generic marker, so the type-family guard is a reflection check that the type closes
// IQuery<> rather than an IsAssignableFrom against a marker (the same approach QueryTypeRegistry uses,
// ADR 0022).
internal static class QueryPermissionValidation
{
    public static IReadOnlyCollection<Type> FindUndeclared(IEnumerable<Type> candidateTypes)
        => candidateTypes
            .Where(t => t is { IsAbstract: false } && ImplementsQuery(t))
            .Where(t => !typeof(IAuthorizedQuery).IsAssignableFrom(t))
            .ToArray();

    private static bool ImplementsQuery(Type type)
        => type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuery<>));
}
