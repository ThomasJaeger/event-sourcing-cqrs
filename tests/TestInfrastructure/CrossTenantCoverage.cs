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
    // The required set comes off the port's own declarations rather than from a list anyone
    // maintains, so a write added to a port is required the moment it compiles. What a return
    // type cannot carry alone is the read/write distinction, and the first rule here assumed it
    // could: it required a non-generic Task, read every Task<T> as a read, and so never required
    // a mutation that hands something back. Two of the four ports declare that shape.
    // AdjustOnHandAsync and AdjustReservedAsync return the mutated row's sku through RETURNING,
    // UpdateStatusAsync and MarkReturnedAsync return the affected row's customer id, and all four
    // are statements that write.
    //
    // So the corrected rule takes a member to be a mutation unless the port says otherwise, and
    // the port says otherwise in two ways. void is the notification staging and falls out
    // structurally, since the filter admits only what is awaitable as a Task. Then two names are
    // excluded: CommitAsync, the transaction boundary every case crosses by construction, and any
    // member declared under the Get prefix, which is the only accessor prefix these four ports
    // use and is how a read declares itself here.
    //
    // A name is a weaker signal than a type, so the rule is built to fail in the safe direction.
    // A read introduced under some other prefix is required rather than skipped, and that surfaces
    // as a failing property somebody has to look at. The shape this replaces failed the other way,
    // silently. It was wrong from the commit that introduced it and cost nothing until a family
    // with a value-returning mutation arrived, which is the failure mode a gap detector cannot
    // afford. A write that named itself Get would still escape, and nothing here can see that.
    //
    // Inherited members never reach the filter at all. GetMethods on an interface reports what
    // that interface declares and not what its bases do, so IAsyncDisposable.DisposeAsync is
    // absent from the set rather than filtered out of it. The outcome is the one wanted, since
    // disposal is not a mutation, but it rests on how interface reflection behaves rather than on
    // a rule here, and a base interface that later declares a mutating member would be missed the
    // same way.
    //
    // A write is named by its declaring interface and its own name, joined by a dot, which is the
    // token RecordingPort records. A family wires two ports into one recording set, so bare method
    // names would let a read on one earn coverage for a same-named write on the other.
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
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Where(m => m.Name != "CommitAsync")
            .Where(m => !m.Name.StartsWith("Get", StringComparison.Ordinal))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }
}
