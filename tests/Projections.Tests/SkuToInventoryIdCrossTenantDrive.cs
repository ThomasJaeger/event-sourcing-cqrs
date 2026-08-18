using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.SkuToInventoryId;
using EventSourcingCqrs.TestInfrastructure;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// The SKU-to-InventoryId cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// The aggregate identifier the harness hands a drive is the inventory id here, and it is not what
// the row is keyed on. read_models.sku_to_inventory_id is keyed (tenant_id, sku): migration 0009
// created it on the sku alone, 0017 added the discriminator column, and 0018 moved the key. The sku
// is TEXT and the harness hands a Guid, so the key part both tenants present is the constant below
// rather than anything the harness mints. The supplied Guid is the owner's inventory id, which
// reaches the row's value column, and the acting tenant mints its own and carries it out through
// OtherInventoryIds.
//
// This is the second family whose row key is not the supplied identifier. Order throughput was the
// first, and there the Guid reaches no column at all. Here it reaches one, and that difference is
// what the facts turn on: two tenants writing the same sku under distinct inventory ids leaves two
// rows a read can tell apart, where equal ids would leave two rows differing only in the
// discriminator and a read-back could not say whose mapping it got.
//
// One drive reaches the one mutating member the port declares. Record reaches RecordAsync.
//
// The act aims at the exact sku the owner recorded. RecordAsync resolves its conflict with
// ON CONFLICT (tenant_id, sku) DO NOTHING, so a fresh sku cannot collide, and a drive that cannot
// collide earns coverage without crossing anything.
//
// No raw-SQL seed here, where three of the six families before this one carry one. All three seed
// for the same reason: the act's handler path resolves through a tenant-predicated lookup read
// before it reaches the mutation under test, so the acting tenant needs a lookup row of its own
// that the act does not write. Order detail's return path reads order_detail_shipments, customer
// summary's cancellation path reads customer_summary_orders, and inventory dashboard's release path
// reads inventory_reservations. SkuToInventoryIdProjection.HandleAsync reads the checkpoint and
// calls RecordAsync, with no lookup between them, so this family's write path needs nothing seeded.
//
// The assertions live in SkuToInventoryIdCrossTenantWriteTests, where a reader debugging a failure
// will look. The drives live here so both that class and the harness run the same code.
internal static class SkuToInventoryIdCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    // The one sku both phases present, and the whole of what the two tenants share. Held here
    // rather than minted per run because it is the key part, and the facts name it to read back.
    internal const string Sku = "SKU-SHARED";

    // The inventory id the acting tenant minted, carried out so a fact can name it. Keyed by the
    // owner's inventory id, since each drive runs on its own database with a fresh one.
    private static readonly Dictionary<Guid, Guid> OtherInventoryIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Record", Bind(RecordAsOwner), Bind(RecordOwnersSkuAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "SkuToInventoryId",
        typeof(ISkuToInventoryIdUnitOfWork),
        BuildTarget,
        All);

    internal static Guid OtherInventoryId(Guid ownerInventoryId)
        => OtherInventoryIds[ownerInventoryId];

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<SkuToInventoryIdProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, inventoryId, connStr)
            => drive(
                (SkuToInventoryIdProjection)target.Projection, target.Tenant, inventoryId, connStr);

    // The real adapter, wrapped so the coverage property can see which members a run reached. The
    // wrapper delegates every call, so the isolation property still observes the real writes.
    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        // The store consumes this accessor on both paths, so flipping it between the phases is what
        // makes the act phase a second tenant's write and a fact's read a second tenant's read.
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresSkuToInventoryIdStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<ISkuToInventoryIdStore>(
            real, invoked, typeof(ISkuToInventoryIdUnitOfWork));
        var projection = new SkuToInventoryIdProjection(store);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant records the mapping ===

    // One InventoryCreated at the shared sku, so the owner ends holding exactly one mapping from
    // that sku to the inventory id the harness supplied.
    private static async Task RecordAsOwner(
        SkuToInventoryIdProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new InventoryCreated(inventoryId, Sku, At), 1),
            CancellationToken.None);
    }

    // === act: the second tenant records the same sku against an inventory of its own ===

    // The same sku the owner used, mapped to an inventory id of the acting tenant's own. The insert
    // binds this tenant and conflicts on (tenant_id, sku), so it collides with nothing the owner
    // holds and the acting tenant keeps its own mapping. Under a key on the sku alone it would find
    // the owner's row and DO NOTHING would discard it, leaving that tenant unable to resolve a sku
    // it created inventory for.
    private static async Task RecordOwnersSkuAsOther(
        SkuToInventoryIdProjection p, StubTenantAccessor stub, Guid inventoryId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var otherInventoryId = Guid.NewGuid();
        OtherInventoryIds[inventoryId] = otherInventoryId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(new InventoryCreated(otherInventoryId, Sku, At), 10),
            CancellationToken.None);
    }
}
