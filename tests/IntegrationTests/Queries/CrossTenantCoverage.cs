namespace EventSourcingCqrs.IntegrationTests.Queries;

// The read-side twin of QueryPermissionValidation.FindUndeclared, for ADR 0031's
// cross-tenant coverage mandate: given the query types the host registers and the types
// that have a cross-tenant coverage entry, returns the registered types with no entry.
// A pure function so the meta-test asserts completeness over the host's actual registered
// set and a unit test proves the gap-detection, the same shape the permission-declaration
// walk uses. It lives in the test assembly, not in Application, because coverage is a
// property of the test suite, not of the query type.
internal static class CrossTenantCoverage
{
    public static IReadOnlyCollection<Type> FindUncovered(
        IEnumerable<Type> registered, ISet<Type> covered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(covered);
        return registered.Where(t => !covered.Contains(t)).ToArray();
    }
}
