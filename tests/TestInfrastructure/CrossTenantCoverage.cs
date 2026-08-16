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

    // The second gap FindUncovered cannot see. Registry completeness makes a projection owe one
    // entry and constrains nothing about what the entry does, so a case that drives one creating
    // event and asserts one read satisfies the mandate while every mutation the projection can
    // perform stays unreached. This detector closes over the mutating surface instead of the
    // registry: given a unit-of-work port and the method names a case invoked, it
    // returns the declared mutations no case reached.
    //
    // The required set comes from the type system rather than from a list anyone maintains, so
    // a write method added to a port is required the moment it compiles. A mutation is a member
    // the port itself declares returning non-generic Task: Task<T> is a read and void is the
    // notification staging, and both fall out of the return-type filter. CommitAsync is the
    // transaction boundary every case crosses by construction and is excluded by name, which is
    // the one judgment in the rule and is stated here rather than buried in a caller.
    //
    // Inherited members never reach the filter at all. GetMethods on an interface reports what
    // that interface declares and not what its bases do, so IAsyncDisposable.DisposeAsync is
    // absent from the set rather than filtered out of it. The outcome is the one wanted, since
    // disposal is not a mutation, but it rests on how interface reflection behaves rather than on
    // a rule here, and a base interface that later declares a mutating member would be missed the
    // same way.
    public static IReadOnlyCollection<string> FindUnexercisedWrites(
        Type unitOfWorkPort, ISet<string> exercised)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkPort);
        ArgumentNullException.ThrowIfNull(exercised);
        return DeclaredWrites(unitOfWorkPort).Where(m => !exercised.Contains(m)).ToArray();
    }

    public static IReadOnlyCollection<string> DeclaredWrites(Type unitOfWorkPort)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkPort);
        return unitOfWorkPort.GetMethods()
            .Where(m => m.ReturnType == typeof(Task))
            .Select(m => m.Name)
            .Where(n => n != "CommitAsync")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}
