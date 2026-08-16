using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace EventSourcingCqrs.Projections.Tests;

// The customer-summary cross-tenant drives, in the shape the write-surface harness runs: an
// arrange phase under the owning tenant and an act phase under a second one.
//
// The aggregate identifier the harness hands a drive is the customer id here, because that is what
// customer_summary is keyed on and what both tenants can present. Order ids are minted inside the
// drives and carried between the phases through OwnerOrderIds.
//
// Two drives reach all four writes the port declares. Placement reaches ApplyPlacementAsync and
// InsertOrderAsync; cancellation reaches ApplyCancellationAsync and DeleteOrderAsync.
internal static class CustomerSummaryCrossTenantDrive
{
    private static TenantId Owner => ProjectionTenantTaggingTests.TenantA;
    private static TenantId Other => ProjectionTenantTaggingTests.TenantB;
    private static DateTime At => ProjectionTenantTaggingTests.At;

    internal const decimal OwnerTotal = 100m;
    internal const decimal OtherTotal = 9999m;
    internal const decimal OtherCancelTotal = 55m;

    // The order the owning tenant placed, carried between the phases so an act can aim at the row
    // the owner holds. Keyed by customer id, since each drive runs on its own database.
    private static readonly Dictionary<Guid, Guid> OwnerOrderIds = [];

    internal static IReadOnlyList<CrossTenantDrive> All { get; } =
    [
        new("Place", Bind(PlaceAsOwner), Bind(PlaceOwnersOrderAsOther)),
        new("Cancel", Bind(PlaceAsOwner), Bind(CancelOwnOrderAsOther)),
    ];

    // Declared after All, because a static initializer that reads All must run after it.
    internal static CrossTenantFamily Family { get; } = new(
        "CustomerSummary",
        typeof(ICustomerSummaryUnitOfWork),
        BuildTarget,
        All);

    internal static Guid OwnerOrderId(Guid customerId) => OwnerOrderIds[customerId];

    // The one cast in the family, next to the Build that constructed the instance.
    private static Func<CrossTenantTarget, Guid, string, Task> Bind(
        Func<CustomerSummaryProjection, StubTenantAccessor, Guid, string, Task> drive)
        => (target, customerId, connStr)
            => drive((CustomerSummaryProjection)target.Projection, target.Tenant, customerId, connStr);

    private static CrossTenantTarget BuildTarget(NpgsqlDataSource ds, ISet<string> invoked)
    {
        var factory = new NpgsqlReadModelConnectionFactory(ds);
        var stub = new StubTenantAccessor { Current = Owner };
        var real = new PostgresCustomerSummaryStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(), stub);
        var store = RecordingPort.Wrap<ICustomerSummaryStore>(
            real, invoked, typeof(ICustomerSummaryUnitOfWork));
        var projection = new CustomerSummaryProjection(
            store, NullLogger<CustomerSummaryProjection>.Instance);
        return new CrossTenantTarget(projection, stub, store);
    }

    // === arrange: the owning tenant establishes the rows ===

    private static async Task PlaceAsOwner(
        CustomerSummaryProjection p, StubTenantAccessor stub, Guid customerId, string connStr)
    {
        _ = connStr;
        stub.Current = Owner;
        var orderId = Guid.NewGuid();
        OwnerOrderIds[customerId] = orderId;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(orderId, customerId, new Money(OwnerTotal, Currency.USD), At), 1),
            CancellationToken.None);
    }

    // === act: the second tenant drives one event against the owner's identifiers ===

    // The same customer id and the same order id the owner used. Before migration 0025,
    // ApplyPlacementAsync resolved its conflict on (customer_id) and InsertOrderAsync on
    // (customer_id, order_id), and neither key carried the tenant, so both of the acting tenant's
    // rows landed on the owner's. Both targets lead with the tenant now, so the act writes its own
    // two rows and leaves the owner's alone, which is what the facts over this drive assert.
    private static async Task PlaceOwnersOrderAsOther(
        CustomerSummaryProjection p, StubTenantAccessor stub, Guid customerId, string connStr)
    {
        _ = connStr;
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderPlaced(
                    OwnerOrderIds[customerId],
                    customerId,
                    new Money(OtherTotal, Currency.USD),
                    At),
                10),
            CancellationToken.None);
    }

    // The cancellation path recovers the customer through customer_summary_orders, and that lookup
    // carries the tenant predicate. The acting tenant is seeded with its own lookup row under its
    // own order id, so the lookup resolves for it and the netting statements are what would cross:
    // ApplyCancellationAsync was keyed on customer_id alone, and the customer id is shared.
    //
    // The acting tenant is deliberately not given a row at the owner's exact pair here. That
    // arrangement was impossible while pk_customer_summary_orders was (customer_id, order_id), and
    // migration 0025 is what makes it expressible; the fact that names DeleteOrderAsync arranges it
    // directly rather than through a drive, so a throwing arrangement cannot take both harness
    // properties down with it. Keeping this drive on its own order id leaves the two arrangements
    // independent, so the drive keeps reporting even when that fact does not.
    private static async Task CancelOwnOrderAsOther(
        CustomerSummaryProjection p, StubTenantAccessor stub, Guid customerId, string connStr)
    {
        var orderId = Guid.NewGuid();
        await SeedLookupRowAsync(connStr, customerId, orderId, Other.Value, OtherCancelTotal);
        stub.Current = Other;
        await p.HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderCancelled(orderId, "changed mind", Guid.NewGuid(), At), 11),
            CancellationToken.None);
    }

    internal static async Task SeedLookupRowAsync(
        string connStr, Guid customerId, Guid orderId, Guid tenantId, decimal total)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO read_models.customer_summary_orders " +
            "(customer_id, order_id, total_amount, total_currency, placed_utc, tenant_id) " +
            "VALUES (@customer_id, @order_id, @total_amount, @total_currency, @placed_utc, @tenant_id)";
        cmd.Parameters.AddWithValue("customer_id", NpgsqlDbType.Uuid, customerId);
        cmd.Parameters.AddWithValue("order_id", NpgsqlDbType.Uuid, orderId);
        cmd.Parameters.AddWithValue("total_amount", NpgsqlDbType.Numeric, total);
        cmd.Parameters.AddWithValue("total_currency", NpgsqlDbType.Text, Currency.USD.Code);
        cmd.Parameters.AddWithValue("placed_utc", NpgsqlDbType.TimestampTz, At);
        cmd.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Uuid, tenantId);
        await cmd.ExecuteNonQueryAsync();
    }
}
