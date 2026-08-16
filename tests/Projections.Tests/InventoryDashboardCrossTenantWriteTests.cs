using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The inventory-dashboard family's cross-tenant write facts (ADR 0031's discriminator model).
//
// Two tables, both given the discriminator column by migration 0017. inventory_dashboard sat in a
// partial state until migration 0026: 0018 had swapped its UNIQUE(sku) to UNIQUE(tenant_id, sku)
// and left pk_inventory_dashboard (inventory_id) alone. inventory_reservations kept
// pk_inventory_reservations (inventory_id, order_id, line_id) from 0013, tenant-blind in full. 0026
// moves both to tenant-leading composites and leaves the sku constraint as 0018 left it.
//
// Inventory ids, order ids, and line ids reach the read model from caller input by more than one
// route, and tenants do not, so two tenants can present the same identifiers. The write side keeps
// their streams apart at the stream id; the read model has no such separation. Migration 0026 states
// the stream-id half precisely, and it is worth reading there rather than here: for the default
// tenant StreamId composes no tenant segment at all, so the shorthand two other migrations and the
// two sibling test files still use is false of every stream in the current corpus.
//
// What each write did before the repair, which is what these facts were written against:
//
//   CreateDashboardAsync     tagged the tenant, then conflicted on a bare ON CONFLICT DO NOTHING
//                            covering both constraints, one of which did not carry the tenant
//   AdjustOnHandAsync        carried no tenant predicate, keyed on inventory_id
//   AdjustReservedAsync      carried no tenant predicate, keyed on inventory_id
//   InsertReservationAsync   tagged the tenant, then conflicted on (inventory_id, order_id, line_id)
//   DeleteReservationAsync   carried no tenant predicate, keyed on the same three ids
//
// The repair is the two key swaps, one conflict target moving to follow its key, and the tenant
// predicate on the three writes that lacked one. CreateDashboardAsync's clause names no columns, so
// it followed its key without an edit.
//
// Two groups, different in kind:
//
//  A  The fold sequences the conflict-target enumeration turns up, driven through the projection's
//     own HandleAsync under a flipped tenant accessor rather than by calling an adapter method.
//
//  B  The statement-level facts, one per untenanted write, each arranged as the owner and acted as
//     a second tenant.
public sealed class InventoryDashboardCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public InventoryDashboardCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // === Group A: the fold sequences ===

    [Fact]
    public async Task Two_tenants_creating_the_same_inventory_id_do_not_share_a_dashboard_row()
    {
        var run = await RunAsync("Create");

        run.Stub.Current = TenantB;
        var seenByB = await run.Store.GetBySkuAsync(
            InventoryDashboardCrossTenantDrive.OtherSku, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "tenant B created its own inventory under its own sku, so B's tenant-filtered read must "
            + "return B's dashboard row rather than nothing");
    }

    [Fact]
    public async Task Two_tenants_reserving_the_same_line_each_keep_their_own_reservation_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var family = InventoryDashboardCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var inventoryId = Guid.NewGuid();

        await family.Drive("Release").ArrangeAsOwner(target, inventoryId, connStr);
        var ownerOrderId = InventoryDashboardCrossTenantDrive.OwnerOrderId(inventoryId);
        var ownerLineId = InventoryDashboardCrossTenantDrive.OwnerLineId(inventoryId);

        // The acting tenant reserves the owner's exact line. InsertReservationAsync tagged the
        // tenant and then resolved its conflict against a pk_inventory_reservations that did not
        // carry one, so the acting tenant's lookup row landed on the owner's key and was discarded.
        // With 0026 the key and the target both lead with the tenant, so the two rows coexist.
        target.Tenant.Current = TenantB;
        await ((InventoryDashboardProjection)target.Projection).HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryReserved(
                    inventoryId,
                    ownerOrderId,
                    ownerLineId,
                    InventoryDashboardCrossTenantDrive.OtherSku,
                    InventoryDashboardCrossTenantDrive.OtherReservedQuantity,
                    At),
                20),
            CancellationToken.None);

        target.Tenant.Current = TenantB;
        await using var uow = await Store(target).BeginAsync(CancellationToken.None);
        var seenByB = await uow.GetReservationAsync(
            inventoryId, ownerOrderId, ownerLineId, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "the per-reservation lookup is what a later release recovers the quantity through, so "
            + "tenant B losing its row to the owner's key leaves B unable to net its own release");
    }

    // === Group B: one fact per untenanted write in the family ===

    [Fact]
    public async Task AdjustOnHandAsync_under_another_tenant_does_not_reach_this_tenants_dashboard()
    {
        var run = await RunAsync("Adjust");

        var row = await ReadAsOwnerAsync(run);
        row!.OnHandQuantity.Should().Be(
            0,
            "another tenant's InventoryAdjusted must not add its delta to this tenant's on-hand "
            + "quantity");
    }

    [Fact]
    public async Task AdjustReservedAsync_under_another_tenant_does_not_reach_this_tenants_dashboard()
    {
        var run = await RunAsync("Reserve");

        var row = await ReadAsOwnerAsync(run);
        row!.ReservedQuantity.Should().Be(
            0,
            "another tenant's InventoryReserved must not add its quantity to this tenant's reserved "
            + "quantity");
    }

    // Arranged here rather than as a drive, and the reason is the finding this fact carries.
    //
    // DeleteReservationAsync deletes on the (inventory_id, order_id, line_id) its caller recovered
    // from the acting tenant's own lookup row, so it can only reach the owner's row when both
    // tenants hold a row at the same triple. While pk_inventory_reservations was those three
    // columns that triple was unique across tenants, the arrangement below was not expressible, and
    // the seed raised a duplicate key before the fact reached an assertion. Migration 0026 makes it
    // expressible and the fact then failed on its assertion, which is what the tenant predicate
    // closes. The statement's reach is opened by the key and closed by the predicate, so the two
    // have to land together, and this fact is where that seam was watched.
    //
    // Keeping the arrangement out of the drive list keeps a throwing arrangement from taking both
    // harness properties down with it, since a drive that throws reports nothing about the drives
    // beside it.
    [Fact]
    public async Task DeleteReservationAsync_under_another_tenant_does_not_delete_this_tenants_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var family = InventoryDashboardCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var inventoryId = Guid.NewGuid();

        await family.Drive("Release").ArrangeAsOwner(target, inventoryId, connStr);
        var ownerOrderId = InventoryDashboardCrossTenantDrive.OwnerOrderId(inventoryId);
        var ownerLineId = InventoryDashboardCrossTenantDrive.OwnerLineId(inventoryId);

        await InventoryDashboardCrossTenantDrive.SeedReservationAsync(
            connStr,
            inventoryId,
            ownerOrderId,
            ownerLineId,
            InventoryDashboardCrossTenantDrive.OtherReservedQuantity,
            TenantB.Value);

        target.Tenant.Current = TenantB;
        await ((InventoryDashboardProjection)target.Projection).HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryReleased(inventoryId, ownerOrderId, ownerLineId, "cancelled", At), 21),
            CancellationToken.None);

        target.Tenant.Current = TenantA;
        await using var uow = await Store(target).BeginAsync(CancellationToken.None);
        var ownersRow = await uow.GetReservationAsync(
            inventoryId, ownerOrderId, ownerLineId, CancellationToken.None);
        ownersRow.Should().NotBeNull(
            "another tenant's InventoryReleased must not delete this tenant's per-reservation "
            + "lookup row");
    }

    // === arrangement ===

    private static DateTime At => ProjectionTenantTaggingTests.At;

    private static IInventoryDashboardStore Store(CrossTenantTarget target)
        => (IInventoryDashboardStore)target.Store;

    private sealed record Run(
        IInventoryDashboardStore Store,
        StubTenantAccessor Stub,
        Guid InventoryId,
        NpgsqlDataSource Source);

    // Runs one named drive's two phases against a fresh migrated database, through the same Build
    // the harness uses, and hands back the store so the fact can read as the owner.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = InventoryDashboardCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var inventoryId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, inventoryId, connStr);
        await drive.ActAsOther(target, inventoryId, connStr);
        return new Run(Store(target), target.Tenant, inventoryId, ds);
    }

    private static async Task<InventoryDashboardRow?> ReadAsOwnerAsync(Run run)
    {
        run.Stub.Current = TenantA;
        var row = await run.Store.GetBySkuAsync(
            InventoryDashboardCrossTenantDrive.OwnerSku, CancellationToken.None);
        row.Should().NotBeNull("tenant A created this inventory and must still be able to read it");
        return row;
    }
}
