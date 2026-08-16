using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.Events;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Projections.CustomerSummary;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The customer-summary family's cross-tenant write facts (ADR 0031's discriminator model).
//
// Two tables, both given the discriminator column by migration 0017 and both keyed without it from
// 0012 until 0025: customer_summary on (customer_id) and customer_summary_orders on
// (customer_id, order_id). Customer ids and order ids reach the read model from the command body,
// and tenants do not, so two tenants can present the same pair. The write side keeps their streams
// apart because StreamId composes the tenant; the read model had no such composition.
//
// What each of the four writes the port declares did before 0025 and its repair, which is what
// these facts hold against:
//
//   ApplyPlacementAsync     tagged the tenant, then conflicted on (customer_id) and folded
//   InsertOrderAsync        tagged the tenant, then conflicted on (customer_id, order_id) and folded
//   ApplyCancellationAsync  carried no tenant predicate, keyed on customer_id
//   DeleteOrderAsync        carried no tenant predicate, keyed on customer_id and order_id
//
// The first two are the class the enumeration calls tenant-blind conflict targets, and they cost
// more here than in the order-detail family: order detail's folds are ON CONFLICT DO NOTHING, so
// the second tenant's row was discarded, while ApplyPlacementAsync's conflict branch is DO UPDATE,
// so the second tenant's money was added to the first tenant's counters on its way past.
//
// Two groups, different in kind:
//
//  A  The disclosure sequence, end to end, driven through the projection's own HandleAsync under a
//     flipped tenant accessor rather than by calling an adapter method.
//
//  B  The statement-level facts, one per untenanted write, each arranged as the owner and acted as
//     a second tenant.
public sealed class CustomerSummaryCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public CustomerSummaryCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // === Group A: the disclosure sequence, end to end ===

    [Fact]
    public async Task Two_tenants_placing_for_the_same_customer_do_not_share_a_summary_row()
    {
        var run = await RunAsync("Place");

        run.Stub.Current = TenantB;
        var seenByB = await run.Store.GetAsync(run.CustomerId, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "tenant B placed its own order for this customer, so B's tenant-filtered read must "
            + "return B's summary rather than nothing");
    }

    [Fact]
    public async Task A_second_tenants_placement_does_not_add_to_the_first_tenants_summary()
    {
        var run = await RunAsync("Place");

        var row = await ReadAsOwnerAsync(run);
        row!.OrderCount.Should().Be(
            1,
            "tenant A placed one order, and tenant B's placement must not increment A's count");
        row.LifetimeValue.Amount.Should().Be(
            CustomerSummaryCrossTenantDrive.OwnerTotal,
            "and B's order total must not be added to A's lifetime value");
    }

    [Fact]
    public async Task Two_tenants_placing_the_same_order_each_keep_their_own_lookup_row()
    {
        var run = await RunAsync("Place");
        var orderId = CustomerSummaryCrossTenantDrive.OwnerOrderId(run.CustomerId);

        run.Stub.Current = TenantB;
        await using var uow = await run.Store.BeginAsync(CancellationToken.None);
        var seenByB = await uow.GetOrderByOrderIdAsync(orderId, CancellationToken.None);
        seenByB.Should().NotBeNull(
            "the per-order lookup is what a later cancellation resolves through, so tenant B "
            + "losing its row to the owner's key leaves B unable to net its own cancellation");
    }

    // === Group B: one fact per untenanted write in the family ===

    [Fact]
    public async Task ApplyCancellationAsync_under_another_tenant_does_not_reach_this_tenants_summary()
    {
        var run = await RunAsync("Cancel");

        var row = await ReadAsOwnerAsync(run);
        row!.OrderCount.Should().Be(
            1,
            "another tenant's cancellation must not decrement this tenant's order count");
        row.LifetimeValue.Amount.Should().Be(
            CustomerSummaryCrossTenantDrive.OwnerTotal,
            "and must not subtract its own order total from this tenant's lifetime value");
    }

    // Arranged here rather than as a drive, and the reason is the finding this fact carries.
    //
    // DeleteOrderAsync deletes on the (customer_id, order_id) its caller resolved from the acting
    // tenant's own lookup row, so it can only reach the owner's row when both tenants hold a row
    // at the same pair. While pk_customer_summary_orders was (customer_id, order_id) that pair was
    // unique across tenants, the arrangement below could not exist, and the seed raised a duplicate
    // key before the fact reached an assertion. Migration 0025 is what makes the arrangement
    // expressible, so this statement's reach is opened by the key and closed by the predicate, and
    // the two had to land together. The fact was watched across that seam: with 0025 applied and
    // the predicate not yet added it failed on this assertion rather than on the seed.
    //
    // Keeping the arrangement out of the drive list keeps a throwing arrangement from taking both
    // harness properties down with it, since a drive that throws reports nothing about the drives
    // beside it.
    [Fact]
    public async Task DeleteOrderAsync_under_another_tenant_does_not_delete_this_tenants_lookup_row()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var ds = NpgsqlDataSource.Create(connStr);
        var family = CustomerSummaryCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var customerId = Guid.NewGuid();

        await family.Drive("Place").ArrangeAsOwner(target, customerId, connStr);
        var ownerOrderId = CustomerSummaryCrossTenantDrive.OwnerOrderId(customerId);

        await CustomerSummaryCrossTenantDrive.SeedLookupRowAsync(
            connStr,
            customerId,
            ownerOrderId,
            TenantB.Value,
            CustomerSummaryCrossTenantDrive.OtherCancelTotal);

        target.Tenant.Current = TenantB;
        await ((CustomerSummaryProjection)target.Projection).HandleAsync(
            ProjectionTenantTaggingTests.Ctx(
                new OrderCancelled(ownerOrderId, "changed mind", Guid.NewGuid(), At), 12),
            CancellationToken.None);

        target.Tenant.Current = TenantA;
        await using var uow = await ((ICustomerSummaryStore)target.Store)
            .BeginAsync(CancellationToken.None);
        var ownersRow = await uow.GetOrderByOrderIdAsync(ownerOrderId, CancellationToken.None);
        ownersRow.Should().NotBeNull(
            "another tenant's cancellation must not delete this tenant's per-order lookup row");
    }

    // === arrangement ===

    private static DateTime At => ProjectionTenantTaggingTests.At;

    private sealed record Run(
        ICustomerSummaryStore Store,
        StubTenantAccessor Stub,
        Guid CustomerId,
        NpgsqlDataSource Source);

    // Runs one named drive's two phases against a fresh migrated database, through the same Build
    // the harness uses, and hands back the store so the fact can read as the owner.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = CustomerSummaryCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var customerId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, customerId, connStr);
        await drive.ActAsOther(target, customerId, connStr);
        return new Run((ICustomerSummaryStore)target.Store, target.Tenant, customerId, ds);
    }

    private static async Task<CustomerSummaryRow?> ReadAsOwnerAsync(Run run)
    {
        run.Stub.Current = TenantA;
        var row = await run.Store.GetAsync(run.CustomerId, CancellationToken.None);
        row.Should().NotBeNull("tenant A placed an order and must still be able to read its summary");
        return row;
    }
}
