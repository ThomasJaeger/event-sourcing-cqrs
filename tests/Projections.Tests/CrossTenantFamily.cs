using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// What a projection family owes the cross-tenant write-surface harness, and the whole of it.
// Adding a family is adding one CrossTenantFamily literal and the drive list it names; neither
// property in CrossTenantWriteSurfaceCoverageTests changes.
//
// The target is what a drive is handed: the projection to dispatch through, the tenant accessor to
// flip between the phases, and the store to read back through. Projection and Store are typed as
// object because no interface spans the projections; each family casts once, in its own drive
// file, next to the Build that constructed the instance.
internal sealed record CrossTenantTarget(object Projection, StubTenantAccessor Tenant, object Store);

// One drive, split into a phase that runs as the owning tenant and a phase that runs as a second
// tenant. The split is what lets the harness snapshot the owner's rows between the two, and what
// lets a write count as covered only when a drive reaches it under a tenant that does not own the
// row.
internal sealed record CrossTenantDrive(
    string Name,
    Func<CrossTenantTarget, Guid, string, Task> ArrangeAsOwner,
    Func<CrossTenantTarget, Guid, string, Task> ActAsOther);

internal sealed record CrossTenantFamily(
    string Name,
    Type UnitOfWorkPort,
    Func<NpgsqlDataSource, ISet<string>, CrossTenantTarget> Build,
    IReadOnlyList<CrossTenantDrive> Drives)
{
    internal CrossTenantDrive Drive(string driveName) => Drives.Single(d => d.Name == driveName);
}

// The families the harness runs.
//
// This list is hand-maintained and both properties read it, so a family missing from it is silently
// green in both. That is the registry-completeness gap one level up from where it bit before. What
// closes it is a meta-fact requiring every unit-of-work port reachable from the projection registry
// to appear here; it lands with the last family, because it is red for families whose drives are
// not written yet. Nothing below it is hand-maintained: the required write set comes off the port
// by reflection and the table set off information_schema.
internal static class CrossTenantFamilies
{
    internal static IReadOnlyList<CrossTenantFamily> All { get; } =
    [
        OrderDetailCrossTenantDrive.Family,
        CustomerSummaryCrossTenantDrive.Family,
        InventoryDashboardCrossTenantDrive.Family,
        OrderListCrossTenantDrive.Family,
        OrderIdToPaymentIdCrossTenantDrive.Family,
        OrderThroughputCrossTenantDrive.Family,
    ];

    internal static CrossTenantFamily Named(string name) => All.Single(f => f.Name == name);
}
