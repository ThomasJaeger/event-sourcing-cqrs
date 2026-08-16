using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.InventoryDashboard;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Projections.Tests;

// The inventory-dashboard cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// The aggregate identifier the harness hands a drive is the inventory id here, because that is what
// inventory_dashboard is keyed on and what both tenants can present. Order and line ids are minted
// inside the drives and carried between the phases through the dictionaries below.
//
// Four drives reach all five mutating members the unit-of-work port declares. Create reaches
// CreateDashboardAsync; Adjust reaches AdjustOnHandAsync; Reserve reaches AdjustReservedAsync and
// InsertReservationAsync; Release reaches AdjustReservedAsync and DeleteReservationAsync.
//
// Five is what the coverage property requires as of this commit and three is what it required
// before it. AdjustOnHandAsync and AdjustReservedAsync return Task<string?> rather than Task,
// because each hands back the mutated row's sku through RETURNING, and the old required set was
// the members declaring a non-generic Task return, so it read both as reads and required neither.
// This family is the first with a mutation that returns a value, and the two it has are the two
// that carried untenanted UPDATE statements. The drives were written to reach them regardless, so
// the corrected property found this family already covered; what it changed is that a later drive
// list cannot drop them silently.
//
// That gap sat inside the unit-of-work port and is separate from the store-port gap the harness
// still carries: TruncateAsync is a mutation too, declared on IInventoryDashboardStore rather than
// here, so the property has never seen it on any family. ResetTenantAsync sits outside both ports,
// on ITenantResettable.
internal static class InventoryDashboardCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    internal const string OwnerSku = "SKU-OWNER";
    internal const string OtherSku = "SKU-OTHER";
    internal const int OwnerReservedQuantity = 4;
    internal const int OtherAdjustDelta = 77;
    internal const int OtherReservedQuantity = 9;

    // The reservation line the owning tenant established, and the one the acting tenant minted,
    // carried between the phases so an act can aim at a row it owns. Keyed by inventory id, since
    // each drive runs on its own database with a fresh one.
    private static readonly Dictionary<Guid, Guid> OwnerOrderIds = [];
    private static readonly Dictionary<Guid, Guid> OwnerLineIds = [];
    private static readonly Dictionary<Guid, Guid> OtherOrderIds = [];
    private static readonly Dictionary<Guid, Guid> OtherLineIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Create", Bind(CreateAsOwner), Bind(CreateOwnersInventoryIdAsOther)),
        new("Adjust", Bind(CreateAsOwner), Bind(AdjustOwnersInventoryAsOther)),
        new("Reserve", Bind(CreateAsOwner), Bind(ReserveAgainstOwnersInventoryAsOther)),
        new("Release", Bind(CreateAndReserveAsOwner), Bind(ReleaseOwnLineAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "InventoryDashboard",
        typeof(IInventoryDashboardUnitOfWork),
        BuildTarget,
        All);

    internal static Guid OwnerOrderId(Guid inventoryId) => OwnerOrderIds[inventoryId];

    internal static Guid OwnerLineId(Guid inventoryId) => OwnerLineIds[inventoryId];

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<InventoryDashboardProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, inventoryId, connStr)
            => drive((InventoryDashboardProjection)target.Projection, target.Tenant, inventoryId, connStr);

    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresInventoryDashboardStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<IInventoryDashboardStore>(
            real, invoked, typeof(IInventoryDashboardUnitOfWork));
        var projection = new InventoryDashboardProjection(
            store, NullLogger<InventoryDashboardProjection>.Instance);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the rows ===

    private static async Task CreateAsOwner(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new InventoryCreated(inventoryId, OwnerSku, At), 1),
            CancellationToken.None);
    }

    private static async Task CreateAndReserveAsOwner(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        await CreateAsOwner(p, stub, inventoryId, connStr);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        OwnerOrderIds[inventoryId] = orderId;
        OwnerLineIds[inventoryId] = lineId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryReserved(
                    inventoryId, orderId, lineId, OwnerSku, OwnerReservedQuantity, At), 2),
            CancellationToken.None);
    }

    // === act: the second tenant drives one event against the owner's inventory id ===

    // The same inventory id the owner used, under a sku of the acting tenant's own. Only the
    // primary key could ever fold this: uq_inventory_dashboard_tenant_sku has led with the tenant
    // since 0018, so the two skus never collide. Migration 0026 puts the tenant at the front of
    // pk_inventory_dashboard too, so the insert now conflicts on neither and the acting tenant
    // gets its own row. The clause names no columns, so it needed no edit to follow the key.
    private static async Task CreateOwnersInventoryIdAsOther(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new InventoryCreated(inventoryId, OtherSku, At), 10),
            CancellationToken.None);
    }

    private static async Task AdjustOwnersInventoryAsOther(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryAdjusted(inventoryId, OtherSku, OtherAdjustDelta, "restock", At), 11),
            CancellationToken.None);
    }

    // The acting tenant reserves against the owner's inventory id under a line of its own. Before
    // the repair AdjustReservedAsync keyed on inventory_id alone, so the reserved counter it moved
    // was the owner's; it now carries the tenant predicate and moves nothing.
    // InsertReservationAsync writes the acting tenant's own lookup row, which the key admits
    // either way here, because the line is fresh.
    private static async Task ReserveAgainstOwnersInventoryAsOther(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        OtherOrderIds[inventoryId] = orderId;
        OtherLineIds[inventoryId] = lineId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryReserved(
                    inventoryId, orderId, lineId, OtherSku, OtherReservedQuantity, At), 12),
            CancellationToken.None);
    }

    // The release path recovers the quantity through inventory_reservations, and that lookup has
    // always carried the tenant predicate. The acting tenant is seeded with its own reservation row
    // under its own line, so the lookup resolves for it and the netting statements are what used to
    // cross: AdjustReservedAsync keyed on inventory_id alone against a shared inventory id.
    //
    // The acting tenant is deliberately not given a row at the owner's exact line here. That
    // arrangement was not expressible while pk_inventory_reservations was
    // (inventory_id, order_id, line_id), and the fact that names DeleteReservationAsync arranges it
    // directly rather than through a drive, so a throwing arrangement cannot take both harness
    // properties down with it. Migration 0026 makes it expressible, and that fact keeps its own
    // arrangement anyway: what the drive list must not depend on is a key, and a key can move.
    private static async Task ReleaseOwnLineAsOther(
        InventoryDashboardProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        OtherOrderIds[inventoryId] = orderId;
        OtherLineIds[inventoryId] = lineId;
        await SeedReservationAsync(
            connStr, inventoryId, orderId, lineId, OtherReservedQuantity, Other.Value);
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new InventoryReleased(inventoryId, orderId, lineId, "cancelled", At), 13),
            CancellationToken.None);
    }

    internal static async Task SeedReservationAsync(
        string connStr, Guid inventoryId, Guid orderId, Guid lineId, int quantity, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.inventory_reservations " +
            "(inventory_id, order_id, line_id, quantity, reserved_utc, tenant_id) " +
            "VALUES (@inventory_id, @order_id, @line_id, @quantity, @reserved_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("inventory_id", NpgsqlDbType.Uuid, inventoryId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("line_id", NpgsqlDbType.Uuid, lineId);
        cmd.Parameters.AddWithValue("quantity", NpgsqlDbType.Integer, quantity);
        cmd.Parameters.AddWithValue("reserved_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }
}
