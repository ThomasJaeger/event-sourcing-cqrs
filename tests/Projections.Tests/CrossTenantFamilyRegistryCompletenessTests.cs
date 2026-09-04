using System;
using System.Linq;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The registry-completeness guard for the cross-tenant write-surface harness.
//
// Why this exists
//   CrossTenantFamilies.All is hand-maintained, and both properties in
//   CrossTenantWriteSurfaceCoverageTests read it. A read-model family missing from that list is
//   therefore silently green in both: the harness reports full coverage of the families it was
//   told about, and says nothing about the one it was not. That is the gap one level up from the
//   coverage the harness already gives, and the comment above CrossTenantFamilies has named it
//   since the harness was built.
//
//   The done-criterion this closes asks that no query, command, subscription or projection reach
//   production without a cross-tenant isolation test, enforced structurally so a registered type
//   that lacks coverage fails the suite. Hand-maintenance is the opposite of structural: it fails
//   only when somebody remembers. This fact makes forgetting impossible instead.
//
// Why it can land now and could not before
//   The fact is red for any unit-of-work port whose drives are not written yet, so it had to wait
//   for the last family. The eighth landed with the current-user-roles drives, and eight ports now
//   meet eight families.
//
// What it reads, and what it deliberately does not
//   The port set comes off the Domain assembly by reflection over the I*UnitOfWork suffix, so a
//   ninth read model added tomorrow enters this set the moment its port is declared, with no edit
//   here. The family set comes off CrossTenantFamilies.All. Nothing in either is a literal list
//   maintained beside the thing it describes, which is what makes this a guard rather than a
//   second copy of the problem.
//
//   It does not walk the projection registry to reach the ports. Projections take their store and
//   receive the unit of work as a method parameter, so a registry walk would have to inspect method
//   signatures to find what an assembly scan finds directly. The suffix convention is pinned by the
//   naming fact below, so the scan cannot quietly stop matching.
public class CrossTenantFamilyRegistryCompletenessTests
{
    // Any type in the Domain assembly that ends in UnitOfWork is a write surface a projection
    // commits through, and every one of them owes the harness a family.
    private static Type[] DeclaredUnitOfWorkPorts() =>
        typeof(IOrderDetailUnitOfWork).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.Name.EndsWith("UnitOfWork", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Every_declared_unit_of_work_port_has_a_family_in_the_harness()
    {
        var declared = DeclaredUnitOfWorkPorts();
        var covered = CrossTenantFamilies.All.Select(f => f.UnitOfWorkPort).ToArray();

        // BeEquivalentTo in both directions at once: a port with no family fails, and a family
        // naming a port the assembly no longer declares fails too. The second half matters because
        // a renamed port would otherwise leave a family pointing at nothing and still read green.
        covered.Should().BeEquivalentTo(
            declared,
            "every unit-of-work port is a cross-tenant write surface and owes the harness a family, "
            + "and every family owes a port that still exists");
    }

    [Fact]
    public void No_family_is_registered_twice()
    {
        // Two families naming one port would satisfy the set comparison above while leaving a
        // second port uncovered, because a set comparison is blind to multiplicity.
        var ports = CrossTenantFamilies.All.Select(f => f.UnitOfWorkPort).ToArray();

        ports.Should().OnlyHaveUniqueItems();
        CrossTenantFamilies.All.Select(f => f.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_family_declares_at_least_one_drive()
    {
        // A family with an empty drive list passes the completeness fact above and covers nothing.
        // The coverage properties would report it as having no required writes reached, but they
        // read the same hand-maintained list, so this is the fact that says an entry must do work.
        foreach (var family in CrossTenantFamilies.All)
        {
            family.Drives.Should().NotBeEmpty($"family {family.Name} exists to run drives");
        }
    }

    [Fact]
    public void The_suffix_convention_the_scan_depends_on_still_matches_something()
    {
        // The scan above is only as good as the naming convention it assumes. If the ports were
        // ever renamed away from the suffix, DeclaredUnitOfWorkPorts would return an empty set and
        // the completeness fact would pass vacuously against an empty family list. This is the
        // guard against that: the convention has to keep finding ports.
        DeclaredUnitOfWorkPorts().Should().NotBeEmpty(
            "the completeness fact scans for the UnitOfWork suffix and passes vacuously without it");
    }
}
