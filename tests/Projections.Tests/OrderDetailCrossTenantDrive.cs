using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.Projections.OrderDetail;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Projections.Tests;

// The order-detail cross-tenant drives, split into an arrange phase that runs as the owning
// tenant and an act phase that runs as a second tenant. The split is what lets the write-surface
// harness snapshot the owning tenant's rows between the two and assert the act changed none of
// them, and it is what lets a write count as covered only when a fact drives it under a tenant
// that does not own the row.
//
// The assertions live in OrderDetailCrossTenantWriteTests, where a reader debugging a failure
// will look. The drives live here so both that class and the harness run the same code.
internal static class OrderDetailCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    // A line and a shipment the owner created, carried between the phases so the act can aim at
    // exactly the row the owner owns. Keyed by order id, since each drive runs on its own
    // database with a fresh order id.
    private static readonly Dictionary<Guid, Guid> OwnerLineIds = [];
    private static readonly Dictionary<Guid, Guid> OwnerShipmentIds = [];
    private static readonly Dictionary<Guid, Guid> OwnerPaymentIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Draft", Bind(DraftAsOwner), Bind(DraftAsOther)),
        new("Place", Bind(DraftAsOwner), Bind(PlaceAsOther)),
        new("Ship", Bind(DraftAsOwner), Bind(ShipAsOther)),
        new("Cancel", Bind(DraftAsOwner), Bind(CancelAsOther)),
        new("Complete", Bind(DraftAsOwner), Bind(CompleteAsOther)),
        new("SetAddress", Bind(DraftAsOwner), Bind(SetAddressAsOther)),
        new("Return", Bind(DraftAndScheduleShipmentAsOwner), Bind(ReturnAsOther)),
        new("RemoveLine", Bind(DraftAndAddLineAsOwner), Bind(RemoveOwnersLineAsOther)),
        new("AddLine", Bind(DraftAsOwner), Bind(AddLineAsOther)),
        new("ScheduleShipment", Bind(DraftAndScheduleShipmentAsOwner), Bind(ScheduleOwnersShipmentIdAsOther)),
        new("AuthorizePayment", Bind(DraftAndAuthorizePaymentAsOwner), Bind(AuthorizeOwnersPaymentIdAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "OrderDetail",
        typeof(IOrderDetailUnitOfWork),
        BuildTarget,
        All);

    // The one cast in the family, next to the Build that constructed the instance. Drive bodies
    // below stay typed against the projection, so an event a projection does not handle is a
    // compile error rather than a runtime one.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<OrderDetailProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, orderId, connStr)
            => drive((OrderDetailProjection)target.Projection, target.Tenant, orderId, connStr);

    // The real adapter, wrapped so the coverage property can see which members a run reached. The
    // wrapper delegates every call, so the isolation property still observes the real writes.
    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresOrderDetailStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<IOrderDetailStore>(
            real, invoked, typeof(IOrderDetailUnitOfWork));
        var projection = new OrderDetailProjection(
            store, EventStoreJsonOptions.Create(), NullLogger<OrderDetailProjection>.Instance);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the rows ===

    private static async Task DraftAsOwner(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), 1),
            CancellationToken.None);
    }

    private static async Task DraftAndAddLineAsOwner(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        await DraftAsOwner(p, stub, orderId, connStr);
        var lineId = Guid.NewGuid();
        OwnerLineIds[orderId] = lineId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderLineAdded(orderId, lineId, "SKU-A", 1, new Money(5m, Currency.USD), At), 2),
            CancellationToken.None);
    }

    private static async Task DraftAndScheduleShipmentAsOwner(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        await DraftAsOwner(p, stub, orderId, connStr);
        var shipmentId = Guid.NewGuid();
        OwnerShipmentIds[orderId] = shipmentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentScheduled(shipmentId, orderId, SampleAddress(), [], At), 2),
            CancellationToken.None);
    }

    // === act: the second tenant drives one event at the same order id ===

    private static async Task DraftAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderDrafted(orderId, Guid.NewGuid(), At, "web"), 10),
            CancellationToken.None);
    }

    private static async Task PlaceAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, Guid.NewGuid(), new Money(9999m, Currency.USD), At), 11),
            CancellationToken.None);
    }

    private static async Task ShipAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderShipped(orderId, "ACME", "TRK-1", At), 12),
            CancellationToken.None);
    }

    private static async Task CancelAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderCancelled(orderId, "changed mind", Guid.NewGuid(), At), 13),
            CancellationToken.None);
    }

    private static async Task CompleteAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new OrderCompleted(orderId, At), 14),
            CancellationToken.None);
    }

    private static async Task SetAddressAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShippingAddressSet(
                    orderId, new Address("9 Other St", "Elsewhere", "99999", "GB"), At), 15),
            CancellationToken.None);
    }

    // The return path resolves the order through order_detail_shipments, and that lookup does
    // carry the tenant predicate. The mapping the owner created is copied into the acting
    // tenant by raw SQL under a fresh shipment id, so the lookup resolves under the acting
    // tenant and MarkReturnedAsync is the only untenanted thing left in the path.
    private static async Task ReturnAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        var shipmentId = Guid.NewGuid();
        await SeedShipmentMappingAsync(connStr, shipmentId, orderId, Other.Value);
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new ShipmentReturned(shipmentId, "damaged", At), 16),
            CancellationToken.None);
    }

    // Aimed at the exact (order_id, line_id) the owner created. order_detail_lines is keyed on
    // that pair, so the row exists once across the whole table and belongs to the owner.
    private static async Task RemoveOwnersLineAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderLineRemoved(orderId, OwnerLineIds[orderId], At), 17),
            CancellationToken.None);
    }

    private static async Task AddLineAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderLineAdded(orderId, Guid.NewGuid(), "SKU-B", 2, new Money(7m, Currency.USD), At), 18),
            CancellationToken.None);
    }

    // order_detail_shipments was keyed on shipment_id alone and order_detail_payments on
    // payment_id alone, both caller-supplied through ScheduleShipment and AuthorizePayment, and
    // both inserts carry ON CONFLICT DO NOTHING. A drive acting on a fresh id cannot collide, so
    // it would earn coverage without crossing a boundary. Each act reuses the id the owner
    // established, which is the collision the tenant-blind key admitted.
    private static async Task ScheduleOwnersShipmentIdAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new ShipmentScheduled(OwnerShipmentIds[orderId], orderId, SampleAddress(), [], At), 19),
            CancellationToken.None);
    }

    private static async Task DraftAndAuthorizePaymentAsOwner(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        await DraftAsOwner(p, stub, orderId, connStr);
        var paymentId = Guid.NewGuid();
        OwnerPaymentIds[orderId] = paymentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new PaymentAuthorized(paymentId, orderId, new Money(10m, Currency.USD), "ref-a", At), 3),
            CancellationToken.None);
    }

    private static async Task AuthorizeOwnersPaymentIdAsOther(
        OrderDetailProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new PaymentAuthorized(
                    OwnerPaymentIds[orderId], orderId, new Money(10m, Currency.USD), "ref-b", At), 20),
            CancellationToken.None);
    }

    internal static Guid OwnerLineId(Guid orderId) => OwnerLineIds[orderId];

    private static Address SampleAddress() => new("1 Main St", "Smalltown", "12345", "US");

    private static async Task SeedShipmentMappingAsync(
        string connStr, Guid shipmentId, Guid orderId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.order_detail_shipments " +
            "(shipment_id, order_id, scheduled_utc, tenant_id) " +
            "VALUES (@shipment_id, @order_id, @scheduled_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("shipment_id", NpgsqlDbType.Uuid, shipmentId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("scheduled_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }
}
