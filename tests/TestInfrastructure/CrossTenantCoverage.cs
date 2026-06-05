namespace EventSourcingCqrs.TestInfrastructure;

// Surface-agnostic gap detector for ADR 0031's cross-tenant coverage mandate: given a host's
// registered types and the types that carry a coverage entry, returns the registered types with no
// entry. A pure function shared by the coverage meta-tests, so it lives in the shared test-support
// assembly rather than in any one test project.
public static class CrossTenantCoverage
{
    public static IReadOnlyCollection<Type> FindUncovered(
        IEnumerable<Type> registered, ISet<Type> covered)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(covered);
        return registered.Where(t => !covered.Contains(t)).ToArray();
    }
}
