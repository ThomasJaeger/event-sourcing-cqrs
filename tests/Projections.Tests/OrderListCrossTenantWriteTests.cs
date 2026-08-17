using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The order-list family's cross-tenant write facts (ADR 0031's discriminator model).
//
// Two tables, both given the discriminator column by migration 0017, and both keyed on a single id
// until 0027: order_list on pk_order_list (order_id) from 0004, and order_list_shipments on
// pk_order_list_shipments (shipment_id) from 0011. 0027 puts the tenant at the front of each.
//
// The two ids do not have the same origin, and the distinction is worth keeping straight. Order ids
// reach this read model from caller input, since DraftOrder and PlaceOrder both declare an OrderId
// and both are HTTP-dispatchable by name. Shipment ids do not: the OrderFulfillment process manager
// mints one and dispatches ScheduleShipment itself, and no command type provider registers that
// command. So the order_list fold was reachable through a route and the shipments fold was not.
// Both facts below drive through the projection either way, because what they hold is that the key
// separates the tenants rather than that a caller can currently make it matter.
//
// What each write did before 0027, which is the shape these facts were written against:
//
//   InsertAsync                  tagged the tenant, then conflicted on (order_id), which the key
//                                did not carry the tenant in
//   UpdateStatusAsync            carried AND tenant_id = @tenant
//   InsertShipmentMappingAsync   tagged the tenant, then conflicted on (shipment_id), same shape
//   GetOrderIdByShipmentIdAsync  carried AND tenant_id = @tenant
//   MarkReturnedAsync            carried AND tenant_id = @tenant
//
// So this family has no untenanted mutation, and every fact below is a fold sequence rather than a
// statement-level one. Each is driven through the projection's own HandleAsync under a flipped
// tenant accessor rather than by calling an adapter method, because a fold is a property of the
// conflict target and the key together and only the dispatched event exercises both.
//
// The assertions read as the second tenant rather than as the owner. A discarded insert leaves the
// owner's rows exactly as they were, so a fact that watched the owner would pass while the second
// tenant silently held nothing.
public sealed class OrderListCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public OrderListCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_tenants_placing_the_same_order_id_do_not_share_an_order_list_row()
    {
        var run = await RunAsync("Place");

        run.Stub.Current = TenantB;
        var seenByB = await run.Store.GetAsync(run.OrderId, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "tenant B placed its own order under its own order id, so B's tenant-filtered read must "
            + "return B's list row rather than nothing");
    }

    [Fact]
    public async Task Two_tenants_scheduling_the_same_shipment_id_each_keep_their_own_mapping()
    {
        var run = await RunAsync("Schedule");
        var shipmentId = OrderListCrossTenantDrive.OwnerShipmentId(run.OrderId);
        var otherOrderId = OrderListCrossTenantDrive.OtherOrderId(run.OrderId);

        run.Stub.Current = TenantB;
        await using var uow = await run.Store.BeginAsync(CancellationToken.None);
        var resolved = await uow.GetOrderIdByShipmentIdAsync(shipmentId, CancellationToken.None);
        resolved.Should().Be(
            otherOrderId,
            "the shipment-to-order mapping is what a later ShipmentReturned resolves through, so "
            + "tenant B losing its mapping to the owner's key leaves B unable to mark its own "
            + "order returned");
    }

    // === arrangement ===

    private sealed record Run(
        IOrderListStore Store,
        StubTenantAccessor Stub,
        Guid OrderId,
        NpgsqlDataSource Source);

    // Runs one named drive's two phases against a fresh migrated database, through the same Build
    // the harness uses, and hands back the store so the fact can read as the acting tenant.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = OrderListCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var orderId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, orderId, connStr);
        await drive.ActAsOther(target, orderId, connStr);
        return new Run((IOrderListStore)target.Store, target.Tenant, orderId, ds);
    }
}
