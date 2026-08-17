using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderThroughput;
using EventSourcingCqrs.TestInfrastructure;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// The order-throughput cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// Two things here differ from the five families before this one, and a reader arriving from them
// will assume both wrongly.
//
// The aggregate identifier the harness hands a drive is a correlation key here rather than a row
// key. Every other family is keyed on an id a caller supplies, so its act aims at the owner's id
// and the key is what must hold the tenants apart. This table is keyed on (tenant_id, second_utc),
// and the second comes from EventMetadata.OccurredUtc rather than from anything in the payload. The
// drives below carry the supplied Guid through as the order id, where it reaches no column at all,
// so what the two phases share is the second and never the id. The payload amount is inert for the
// same reason: Shape A counts the event and discards the rest of it.
//
// Two drives exist rather than one because the two required writes need arrangements that cannot
// hold at once. Either drive alone satisfies the coverage property, since HandleOrderEventAsync
// calls IncrementSecondAsync and then PruneBeforeAsync on every event past the checkpoint guard, so
// one act phase reaches both. The isolation property is what needs two. To aim it at the upsert's
// conflict target, the act must write the second the owner already holds, so a key on the second
// alone would collide. To aim it at the prune's tenant predicate, the act must write a second far
// enough past the owner's that the cutoff the projection derives lands beyond the owner's bucket,
// and the retention window is 300 seconds. One act cannot do both: equal seconds put the cutoff 300
// seconds before a second the owner occupies, and a gap wide enough to move the cutoff past the
// owner's bucket puts the upsert on a second no other row holds. Count is the first arrangement and
// Prune is the second.
//
// Prune's act is the one place in this file that does not build its event with the shared Ctx. That
// builder fixes OccurredUtc to the shared At, which every other family can take because no other
// read-model key derives from it. The local builder below opens the occurrence instant and changes
// nothing else. Its sibling is the private Context helper in OrderThroughputProjectionTests, which
// exists for the same reason.
//
// The assertions live in OrderThroughputCrossTenantWriteTests, where a reader debugging a failure
// will look. The drives live here so both that class and the harness run the same code.
internal static class OrderThroughputCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    // The payload amount reaches no read-model column, so one value serves both tenants.
    private static Money Amount => new(10m, Currency.USD);

    // How far past the owner's second Prune's act writes. Wider than the projection's 300-second
    // retention window, so the cutoff that act derives lands past the owner's bucket and the tenant
    // predicate is the only thing standing between the prune and it.
    internal const int PruneActOffsetSeconds = 600;

    // The two seconds the facts read back, derived here so the drives and the facts cannot drift.
    internal static DateTime OwnerSecond => At;

    internal static DateTime PruneActSecond => At.AddSeconds(PruneActOffsetSeconds);

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Count", Bind(CountAsOwner), Bind(CountSameSecondAsOther)),
        new("Prune", Bind(CountAsOwner), Bind(CountLaterSecondAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "OrderThroughput",
        typeof(IOrderThroughputUnitOfWork),
        BuildTarget,
        All);

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<OrderThroughputProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, orderId, connStr)
            => drive((OrderThroughputProjection)target.Projection, target.Tenant, orderId, connStr);

    // The real adapter, wrapped so the coverage property can see which members a run reached. The
    // wrapper delegates every call, so the isolation property still observes the real writes.
    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<IOrderThroughputStore>(
            real, invoked, typeof(IOrderThroughputUnitOfWork));
        var projection = new OrderThroughputProjection(store);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes one bucket at its own second ===

    // One event at At, so the owner ends holding exactly one bucket at that second with a count of
    // one. The prune this event carries cuts 300 seconds earlier and matches nothing.
    private static async Task CountAsOwner(
        OrderThroughputProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, Guid.NewGuid(), Amount, At), 1),
            CancellationToken.None);
    }

    // === act: the second tenant counts against the owner's bucket set ===

    // The same second the owner holds. The insert binds this tenant and conflicts on
    // (tenant_id, second_utc), so it writes a row of its own. Under a key on the second alone it
    // would find the owner's row and its DO UPDATE would raise the owner's count.
    private static async Task CountSameSecondAsOther(
        OrderThroughputProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, Guid.NewGuid(), Amount, At), 10),
            CancellationToken.None);
    }

    // A second 600 past the owner's, so the prune this event carries cuts 300 seconds past the
    // owner's bucket rather than before it. The tenant predicate is what spares the owner, and the
    // acting tenant's own bucket sits beyond the cutoff and survives.
    private static async Task CountLaterSecondAsOther(
        OrderThroughputProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var later = PruneActSecond;
        await p.HandleAsync(
            CtxAt(new OrderPlaced(orderId, Guid.NewGuid(), Amount, later), 11, later),
            CancellationToken.None);
    }

    // ProjectionTenantTaggingTests.Ctx with the occurrence instant opened up and nothing else
    // changed. Local to this file, because this is the one family whose read-model key derives from
    // that instant.
    private static EventContext<TEvent> CtxAt<TEvent>(
        TEvent @event, long position, DateTime occurredUtc, TenantId? tenant = null)
        where TEvent : IDomainEvent
        => new(
            @event,
            new EventMetadata(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                CausationId: Guid.NewGuid(),
                ActorId: Guid.Empty,
                Source: "test",
                OccurredUtc: occurredUtc,
                Tenant: tenant ?? WellKnownTenants.Default),
            position);
}
