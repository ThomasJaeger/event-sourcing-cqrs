using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderList;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// The order-list cross-tenant drives, in the shape the write-surface harness runs: an arrange phase
// under the owning tenant and an act phase under a second one.
//
// The aggregate identifier the harness hands a drive is the order id, because that is what
// order_list is keyed on and what both tenants can present. Shipment ids are minted inside the
// drives and carried between the phases through the dictionaries below.
//
// This family's defect sat entirely in the conflict-target class. Both UPDATE statements on the
// port, UpdateStatusAsync and MarkReturnedAsync, already carried AND tenant_id = @tenant, so
// neither ever reached another tenant's row. What did not carry the tenant was the two ON CONFLICT
// targets: InsertAsync conflicted on (order_id) against pk_order_list (order_id), and
// InsertShipmentMappingAsync on (shipment_id) against pk_order_list_shipments (shipment_id). A
// second tenant writing at an id the first tenant already held was discarded, so it ended with no
// row rather than with the wrong one. Migration 0027 moves both keys to tenant-leading composites
// and moves both targets with them. No predicate lands, because none was missing.
//
// That fold shape is why the isolation property passed here while the family was still defective,
// and the property sees no better now. It digests the owning tenant's rows before and after the act
// phase, so what it answers is whether the acting tenant disturbed the owner, and a write the
// acting tenant loses disturbs the owner not at all. The two fold facts beside this file carry the
// complementary assertion. They read back as the second tenant and hold that its own row survived,
// which is the half the property structurally cannot reach.
//
// Four drives reach all four mutating members the port declares. Place reaches InsertAsync; Ship
// reaches UpdateStatusAsync; Schedule reaches InsertShipmentMappingAsync; Return reaches
// InsertShipmentMappingAsync and MarkReturnedAsync.
internal static class OrderListCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    internal static readonly Money OwnerTotal = new(10m, Currency.USD);
    internal static readonly Money OtherTotal = new(99m, Currency.USD);

    // The shipment the owning tenant scheduled, and the one the acting tenant minted, carried
    // between the phases so an act can aim at a row it owns. Keyed by order id, since each drive
    // runs on its own database with a fresh one.
    private static readonly Dictionary<Guid, Guid> OwnerShipmentIds = [];
    private static readonly Dictionary<Guid, Guid> OtherShipmentIds = [];
    private static readonly Dictionary<Guid, Guid> OtherOrderIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Place", Bind(PlaceAsOwner), Bind(PlaceOwnersOrderIdAsOther)),
        new("Ship", Bind(PlaceAsOwner), Bind(ShipOwnersOrderAsOther)),
        new("Schedule", Bind(PlaceAndScheduleAsOwner), Bind(ScheduleOwnersShipmentIdAsOther)),
        new("Return", Bind(PlaceAndScheduleAsOwner), Bind(ReturnOwnShipmentAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "OrderList",
        typeof(IOrderListUnitOfWork),
        BuildTarget,
        All);

    internal static Guid OwnerShipmentId(Guid orderId) => OwnerShipmentIds[orderId];

    internal static Guid OtherShipmentId(Guid orderId) => OtherShipmentIds[orderId];

    internal static Guid OtherOrderId(Guid orderId) => OtherOrderIds[orderId];

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<OrderListProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, orderId, connStr)
            => drive((OrderListProjection)target.Projection, target.Tenant, orderId, connStr);

    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresOrderListStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<IOrderListStore>(
            real, invoked, typeof(IOrderListUnitOfWork));
        var projection = new OrderListProjection(store, NullLogger<OrderListProjection>.Instance);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the rows ===

    private static async Task PlaceAsOwner(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, Guid.NewGuid(), OwnerTotal, At), 1),
            CancellationToken.None);
    }

    private static async Task PlaceAndScheduleAsOwner(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        await PlaceAsOwner(p, stub, orderId, connStr);
        var shipmentId = Guid.NewGuid();
        OwnerShipmentIds[orderId] = shipmentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentScheduled(shipmentId, orderId, Destination, [], At), 2),
            CancellationToken.None);
    }

    // === act: the second tenant drives one event against the owner's ids ===

    // The same order id the owner used, under a customer and total of the acting tenant's own.
    // Before 0027 pk_order_list was (order_id) alone, so the acting tenant's insert conflicted on it
    // and DO NOTHING discarded the row. Under (tenant_id, order_id) the two rows coexist.
    private static async Task PlaceOwnersOrderIdAsOther(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, Guid.NewGuid(), OtherTotal, At), 10),
            CancellationToken.None);
    }

    // UpdateStatusAsync already carries the tenant predicate, so this act matches no row and the
    // handler stages nothing. The drive exists to reach the member under a second tenant, which is
    // what the coverage property counts.
    private static async Task ShipOwnersOrderAsOther(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderShipped(orderId, "ACME", "TRACK-OTHER", At), 11),
            CancellationToken.None);
    }

    // The same shipment id the owner used, mapped to an order of the acting tenant's own. Before
    // 0027 pk_order_list_shipments was (shipment_id) alone, so the acting tenant's mapping
    // conflicted on it and was discarded, leaving that tenant unable to resolve its own shipment
    // later. Under (tenant_id, shipment_id) each tenant keeps its own mapping.
    private static async Task ScheduleOwnersShipmentIdAsOther(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var otherOrderId = Guid.NewGuid();
        OtherOrderIds[orderId] = otherOrderId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentScheduled(
                    OwnerShipmentIds[orderId], otherOrderId, Destination, [], At), 12),
            CancellationToken.None);
    }

    // The acting tenant schedules a shipment of its own against the owner's order id, then returns
    // it. Its own mapping lands, because the shipment id is fresh, so the tenant-predicated lookup
    // resolves and MarkReturnedAsync runs under the acting tenant. That update carries the tenant,
    // so it matches nothing and the owner's return state is untouched.
    private static async Task ReturnOwnShipmentAsOther(
        OrderListProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var shipmentId = Guid.NewGuid();
        OtherShipmentIds[orderId] = shipmentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentScheduled(shipmentId, orderId, Destination, [], At), 13),
            CancellationToken.None);
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentReturned(shipmentId, "damaged", At), 14),
            CancellationToken.None);
    }

    private static Address Destination => new("1 Main St", "Smalltown", "12345", "US");
}
