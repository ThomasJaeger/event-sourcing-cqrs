using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Billing.Events;
using EventSourcingCqrs.Domain.Billing.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.OrderIdToPaymentId;
using EventSourcingCqrs.TestInfrastructure;
using Npgsql;

namespace EventSourcingCqrs.Projections.Tests;

// The order-id-to-payment-id cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// The aggregate identifier the harness hands a drive is the order id, because that is what
// order_id_to_payment_id is keyed on and what both tenants can present. Payment ids are minted
// inside the drives and carried between the phases through the dictionaries below.
//
// This family carried three defects where the four before it carried one class each. RecordAsync
// named no tenant in its column list, so every row took migration 0017's DEFAULT rather than the
// tenant of the event that produced it. Its conflict target was (order_id) against
// pk_order_id_to_payment_id (order_id) from 0010, so a second tenant's mapping at an order id the
// first already held was discarded. And GetPaymentIdAsync carried no tenant predicate, so it handed
// any caller the mapping whichever tenant wrote it. The third was a disclosure rather than a fold,
// and it is the one the other four families had no member of.
//
// All three are closed. Migration 0028 moves the key to (tenant_id, order_id) and the conflict
// target with it, RecordAsync names tenant_id and binds it from the current-tenant accessor, and
// GetPaymentIdAsync gains AND tenant_id = @tenant resolved from the same accessor. The store takes
// the accessor the way its sibling PostgresSkuToInventoryIdStore already did.
//
// Two consequences for the harness, both worth stating where the drives are.
//
// The stub this target carries is live. The store reads it on the write path and on the read path,
// so flipping it between the phases is what makes the act phase a second tenant's write rather than
// a relabelled repeat of the arrangement.
//
// The isolation property was vacuously green here and is not any more. It digests the owning
// tenant's rows, the owning tenant is TenantA, and before the repair every row this projection wrote
// landed under the default tenant, so TenantA held nothing before the act and nothing after and the
// digests matched by carrying nothing. The arrangement now writes under TenantA, so the digest has
// rows to compare and the property answers a question about this family's data.
//
// One drive reaches the one mutating member the port declares. Record reaches RecordAsync.
internal static class OrderIdToPaymentIdCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    private static Money Amount => new(10m, Currency.USD);

    // The payment the owning tenant authorized and the one the acting tenant authorized, carried
    // between the phases so a fact can name either. Keyed by order id, since each drive runs on its
    // own database with a fresh one.
    private static readonly Dictionary<Guid, Guid> OwnerPaymentIds = [];
    private static readonly Dictionary<Guid, Guid> OtherPaymentIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Record", Bind(AuthorizeAsOwner), Bind(AuthorizeOwnersOrderIdAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "OrderIdToPaymentId",
        typeof(IOrderIdToPaymentIdUnitOfWork),
        BuildTarget,
        All);

    internal static Guid OwnerPaymentId(Guid orderId) => OwnerPaymentIds[orderId];

    internal static Guid OtherPaymentId(Guid orderId) => OtherPaymentIds[orderId];

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<OrderIdToPaymentIdProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, orderId, connStr)
            => drive((OrderIdToPaymentIdProjection)target.Projection, target.Tenant, orderId, connStr);

    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        // The store consumes this accessor on both paths, so flipping it between the phases is what
        // makes the act phase a second tenant's write and the fact's read a second tenant's read.
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresOrderIdToPaymentIdStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<IOrderIdToPaymentIdStore>(
            real, invoked, typeof(IOrderIdToPaymentIdUnitOfWork));
        var projection = new OrderIdToPaymentIdProjection(store);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the mapping ===

    private static async Task AuthorizeAsOwner(
        OrderIdToPaymentIdProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        var paymentId = Guid.NewGuid();
        OwnerPaymentIds[orderId] = paymentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new PaymentAuthorized(paymentId, orderId, Amount, "ref-owner", At), 1, Owner),
            CancellationToken.None);
    }

    // === act: the second tenant authorizes its own payment against the owner's order id ===

    // The same order id the owner used, under a payment of the acting tenant's own. The insert binds
    // this tenant and conflicts on (tenant_id, order_id), so it collides with nothing the owner holds
    // and the acting tenant keeps its own mapping.
    private static async Task AuthorizeOwnersOrderIdAsOther(
        OrderIdToPaymentIdProjection p, StubTenantAccessor stub, Guid orderId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        var paymentId = Guid.NewGuid();
        OtherPaymentIds[orderId] = paymentId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new PaymentAuthorized(paymentId, orderId, Amount, "ref-other", At), 10, Other),
            CancellationToken.None);
    }
}
