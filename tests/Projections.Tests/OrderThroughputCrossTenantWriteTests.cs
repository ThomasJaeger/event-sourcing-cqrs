using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The order-throughput family's cross-tenant write facts (ADR 0031's discriminator model).
//
// One table, read_models.order_throughput, created by migration 0021 already keyed
// (tenant_id, second_utc) and unchanged since. This family carries none of the five defect shapes
// the read-side tenant-key arc names, so these facts add coverage rather than pinning a repair.
//
// Two of the three read back as the acting tenant, which the isolation property never does. That
// property digests the owning tenant's rows across the act, so what it answers is whether the
// acting tenant disturbed the owner, and a write the acting tenant loses disturbs the owner not at
// all. This family's upsert resolves its conflict with DO UPDATE rather than DO NOTHING, so a later
// move of the conflict target turns the acting tenant's write into an increment of a row it does
// not own rather than into a discard. Reading back as the acting tenant is what holds that the
// write landed where the tenant can see it.
//
// The third reads back as the owner. It overlaps the isolation property on purpose: it is the one
// assertion here that fails when an arrange phase writes nothing, which both harness properties
// pass over in silence, since equal empty digests compare equal and the coverage property records
// the member rather than the rows.
public sealed class OrderThroughputCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public OrderThroughputCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_tenants_counting_the_same_second_each_keep_their_own_bucket()
    {
        var run = await RunAsync("Count");

        run.Stub.Current = TenantB;
        var seenByB = await run.Store.GetBucketsAsync(CancellationToken.None);

        seenByB.Should().ContainSingle(
            "tenant B counted its own event into the second tenant A already held, so B's "
            + "tenant-filtered read must return B's own bucket rather than nothing");
        var bucket = seenByB.Single();
        bucket.SecondUtc.Should().Be(OrderThroughputCrossTenantDrive.OwnerSecond);
        bucket.Count.Should().Be(1);
    }

    [Fact]
    public async Task The_acting_tenants_prune_spares_the_bucket_it_just_wrote()
    {
        var run = await RunAsync("Prune");

        run.Stub.Current = TenantB;
        var seenByB = await run.Store.GetBucketsAsync(CancellationToken.None);

        seenByB.Should().ContainSingle(
            "the prune rides the same commit as the count that precedes it and cuts strictly "
            + "below its own second, so the bucket the acting tenant just wrote must survive its "
            + "own prune");
        var bucket = seenByB.Single();
        bucket.SecondUtc.Should().Be(OrderThroughputCrossTenantDrive.PruneActSecond);
        bucket.Count.Should().Be(1);
    }

    [Fact]
    public async Task The_owners_bucket_survives_the_acting_tenants_prune()
    {
        var run = await RunAsync("Prune");

        run.Stub.Current = TenantA;
        var seenByA = await run.Store.GetBucketsAsync(CancellationToken.None);

        seenByA.Should().ContainSingle(
            "tenant A holds one bucket after arranging, and the cutoff tenant B's prune derives "
            + "sits past it, so only the tenant predicate keeps that bucket; a run that ends with "
            + "nothing here has either lost the owner's row or never written it");
        var bucket = seenByA.Single();
        bucket.SecondUtc.Should().Be(OrderThroughputCrossTenantDrive.OwnerSecond);
        bucket.Count.Should().Be(1);
    }

    // === arrangement ===

    private sealed record Run(
        IOrderThroughputStore Store,
        StubTenantAccessor Stub,
        Guid OrderId,
        NpgsqlDataSource Source);

    // Runs one named drive's two phases against a fresh migrated database, through the same Build
    // the harness uses, and hands back the store so the fact can read as either tenant.
    private async Task<Run> RunAsync(string driveName)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = OrderThroughputCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var drive = family.Drive(driveName);
        var orderId = Guid.NewGuid();
        await drive.ArrangeAsOwner(target, orderId, connStr);
        await drive.ActAsOther(target, orderId, connStr);
        return new Run((IOrderThroughputStore)target.Store, target.Tenant, orderId, ds);
    }
}
