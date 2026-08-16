using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The order-detail family's cross-tenant write facts (ADR 0031's discriminator model).
//
// read_models.order_detail is keyed on order_id alone (pk_order_detail, migration 0014) and six
// of its writes carry no tenant predicate. A seventh, DeleteLineAsync, is keyed on
// (order_id, line_id) against order_detail_lines and carries none either. Aggregate ids are
// caller-supplied and tenants are not, so two tenants can present the same order id, and the
// write side keeps them apart only because StreamId composes the tenant into the stream. The
// read model has no such composition.
//
// Two groups, different in kind:
//
//  A  The disclosure sequence, end to end. Driven through the projection's own HandleAsync
//     under two tenants, the path a dispatched event really takes, rather than by calling an
//     adapter method directly. The point is that a correctly filtered read returns foreign
//     data, and a test that calls the write adapter directly cannot show that.
//
//  B  The statement-level facts, one per untenanted write. Each arranges as the owning tenant
//     and acts as a second tenant, then asserts the owner's row is untouched.
//
// The drives live in OrderDetailCrossTenantDrive so the write-surface harness runs the same
// arrange and act; the assertions stay here, where a reader debugging a failure will look. Both
// groups read back through GetHeaderAsync, which does carry the tenant predicate. That is
// deliberate: the leak is not a missing read filter, it is a write that crosses under a read
// filter that works.
public sealed class OrderDetailCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public OrderDetailCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // === Group A: the disclosure sequence, end to end ===

    [Fact]
    public async Task Two_tenants_drafting_the_same_order_id_do_not_share_a_header_row()
    {
        var arr = await RunAsync("Draft");

        arr.Stub.Current = TenantB;
        var seenByB = await arr.Store.GetHeaderAsync(arr.OrderId, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "tenant B drafted its own order, so B's tenant-filtered read must return B's header");
    }

    [Fact]
    public async Task A_second_tenants_placement_does_not_write_into_the_first_tenants_header()
    {
        var arr = await RunAsync("Place");

        // A's own tenant-filtered read must still show the header A drafted, with no total. The
        // read filter is correct; the write that crossed is what this asserts against.
        var row = await ReadAsOwnerAsync(arr);
        row!.Total.Should().BeNull(
            "tenant B's OrderPlaced must not write B's order total into the row tenant A reads");
        row.Status.Should().Be(OrderStatus.Draft, "and must not advance tenant A's status");
    }

    // === Group B: one fact per untenanted write in the order-detail family ===

    [Fact]
    public async Task ApplyShippedAsync_under_another_tenant_does_not_reach_this_tenants_header()
    {
        var row = await ReadAsOwnerAsync(await RunAsync("Ship"));
        row!.ShippedUtc.Should().BeNull(
            "another tenant's OrderShipped must not stamp shipped_utc on this tenant's header");
    }

    [Fact]
    public async Task ApplyCancelledAsync_under_another_tenant_does_not_reach_this_tenants_header()
    {
        var row = await ReadAsOwnerAsync(await RunAsync("Cancel"));
        row!.CancelledUtc.Should().BeNull(
            "another tenant's OrderCancelled must not cancel this tenant's order");
        row.Status.Should().Be(OrderStatus.Draft);
    }

    [Fact]
    public async Task ApplyCompletedAsync_under_another_tenant_does_not_reach_this_tenants_header()
    {
        var row = await ReadAsOwnerAsync(await RunAsync("Complete"));
        row!.CompletedUtc.Should().BeNull(
            "another tenant's OrderCompleted must not complete this tenant's order");
        row.Status.Should().Be(OrderStatus.Draft);
    }

    [Fact]
    public async Task SetShippingAddressAsync_under_another_tenant_does_not_reach_this_tenants_header()
    {
        var row = await ReadAsOwnerAsync(await RunAsync("SetAddress"));
        row!.ShippingAddress.Should().BeNull(
            "another tenant's ShippingAddressSet must not write its address into this tenant's header");
    }

    [Fact]
    public async Task MarkReturnedAsync_under_another_tenant_does_not_reach_this_tenants_header()
    {
        var row = await ReadAsOwnerAsync(await RunAsync("Return"));
        row!.ReturnedUtc.Should().BeNull(
            "another tenant's ShipmentReturned must not stamp returned_utc on this tenant's header");
    }

    [Fact]
    public async Task DeleteLineAsync_under_another_tenant_does_not_reach_this_tenants_line()
    {
        var arr = await RunAsync("RemoveLine");

        arr.Stub.Current = TenantA;
        (await arr.Store.GetLinesAsync(arr.OrderId, CancellationToken.None))
            .Should().ContainSingle(
                "another tenant's OrderLineRemoved must not delete this tenant's line");
    }

    // === arrangement ===

    private sealed record Run(
        IOrderDetailStore Store, StubTenantAccessor Stub, Guid OrderId, NpgsqlDataSource Source);

    // Runs one named drive's arrange-as-owner and act-as-other phases against a fresh migrated
    // database, and hands back the store so the fact can read as the owner.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = OrderDetailCrossTenantDrive.Family;

        // The same Build the harness uses, so a fact and a property see one wiring. The recording
        // set is a throwaway here: these facts assert on rows, not on which members were reached.
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var orderId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, orderId, connStr);
        await drive.ActAsOther(target, orderId, connStr);
        return new Run((IOrderDetailStore)target.Store, target.Tenant, orderId, ds);
    }

    private static async Task<OrderDetailRow?> ReadAsOwnerAsync(Run run)
    {
        run.Stub.Current = TenantA;
        var row = await run.Store.GetHeaderAsync(run.OrderId, CancellationToken.None);
        row.Should().NotBeNull("tenant A drafted this order and must still be able to read it");
        return row;
    }
}
