using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment.ReadModels;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

// The SKU-to-InventoryId family's cross-tenant write facts (ADR 0031's discriminator model).
//
// One table, read_models.sku_to_inventory_id, created by migration 0009 keyed on the sku alone,
// given the discriminator column by 0017, and keyed (tenant_id, sku) by 0018. Its four statements
// were enumerated against the five defect shapes session log 0065 names and match none of them:
// RecordAsync names the tenant in its column list and conflicts on a target that carries it,
// GetInventoryIdAsync carries the tenant predicate, ResetTenantAsync carries it too, and
// TruncateAsync is whole-table by design as the rebuild primitive. So these facts add coverage
// rather than pinning a repair.
//
// The first reads back as the acting tenant, which the isolation property never does, and it is not
// optional for this family. That property digests the owning tenant's rows across the act and
// requires them identical afterwards, so what it answers is whether the acting tenant disturbed the
// owner. RecordAsync resolves its conflict with DO NOTHING, and a write the acting tenant loses
// disturbs the owner not at all, so the property is green under the current key and would be green
// under a tenant-blind one. Reading back as the acting tenant is the half it structurally cannot
// reach. The order-list fold facts are the precedent, and they exist for the same conflict action.
//
// The second reads back as the owner. It overlaps the isolation property on purpose: it is the one
// assertion here that fails when an arrange phase writes nothing, which both harness properties
// pass over in silence, since equal empty digests compare equal and the coverage property records
// the member rather than the rows.
//
// The third is the fail-closed fact for the read. ReadModelTenantIsolationTests carries eight of
// these across four families, and this read sits outside that class by that class's own header: it
// covers the query-bus read-model reads, and GetInventoryIdAsync is a projection-private lookup the
// OrderFulfillment process manager calls before each ReserveInventory dispatch. So the fact lands
// at the family, against the store the drive composes, which is where the behaviour is.
public sealed class SkuToInventoryIdCrossTenantWriteTests : IClassFixture<PostgresFixture>
{
    private static readonly TenantId TenantA = ProjectionTenantTaggingTests.TenantA;
    private static readonly TenantId TenantB = ProjectionTenantTaggingTests.TenantB;

    private readonly PostgresFixture _fixture;

    public SkuToInventoryIdCrossTenantWriteTests(PostgresFixture fixture) => _fixture = fixture;

    // The complementary assertion. Both tenants created inventory for the same sku, and the acting
    // tenant asks for its own mapping. A key that carries the tenant leaves it holding the
    // inventory id it recorded; a key on the sku alone discards its insert and leaves it holding
    // nothing, which the owning tenant's rows never show.
    [Fact]
    public async Task Two_tenants_recording_the_same_sku_each_keep_their_own_mapping()
    {
        var run = await RunAsync(drivePhases: true);
        var otherInventoryId =
            SkuToInventoryIdCrossTenantDrive.OtherInventoryId(run.InventoryId);

        run.Stub.Current = TenantB;
        var resolved = await run.Store.GetInventoryIdAsync(
            SkuToInventoryIdCrossTenantDrive.Sku, CancellationToken.None);

        resolved.Should().Be(
            otherInventoryId,
            "tenant B created its own inventory for this sku, so B's tenant-filtered lookup must "
            + "resolve to B's own inventory id; the process manager reserves against whatever this "
            + "returns, so a tenant left holding nothing here cannot reserve its own stock");
    }

    // The owner's half. Only this assertion fails when an arrange phase writes nothing: the
    // isolation property compares two empty digests and calls them equal, and the coverage property
    // records that the member ran rather than what it wrote.
    [Fact]
    public async Task The_owners_mapping_survives_the_acting_tenants_record()
    {
        var run = await RunAsync(drivePhases: true);

        run.Stub.Current = TenantA;
        var resolved = await run.Store.GetInventoryIdAsync(
            SkuToInventoryIdCrossTenantDrive.Sku, CancellationToken.None);

        resolved.Should().Be(
            run.InventoryId,
            "tenant A recorded this sku against its own inventory before tenant B recorded the "
            + "same sku, so A must still resolve to A's inventory id; a run that ends with "
            + "anything else here has either lost the owner's mapping or never written it");
    }

    // Fail-closed. No phases run, because what this asserts happens before the read reaches the
    // database: GetInventoryIdAsync resolves the tenant as its first statement, so an unset
    // accessor throws rather than returning whichever tenant's mapping an unpredicated read finds.
    [Fact]
    public async Task A_lookup_with_no_tenant_set_throws_MissingTenantContextException()
    {
        var run = await RunAsync(drivePhases: false);

        run.Stub.Current = null;

        await run.Store.Invoking(s => s.GetInventoryIdAsync(
                SkuToInventoryIdCrossTenantDrive.Sku, CancellationToken.None))
            .Should().ThrowAsync<MissingTenantContextException>(
                "a lookup with no tenant on the flow is a dispatch-wiring defect, and this read "
                + "must fail closed on it rather than resolve a sku across every tenant");
    }

    // === arrangement ===

    private sealed record Run(
        ISkuToInventoryIdStore Store,
        StubTenantAccessor Stub,
        Guid InventoryId,
        NpgsqlDataSource Source);

    // Runs the family's one drive against a fresh migrated database, through the same Build the
    // harness uses, and hands back the store so a fact can read as either tenant. The phases are
    // optional: the fail-closed fact wants the composed store and no arrangement, so nothing on
    // disk can explain its result.
    private async Task<Run> RunAsync(bool drivePhases)
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        var ds = NpgsqlDataSource.Create(connStr);
        var family = SkuToInventoryIdCrossTenantDrive.Family;
        var target = family.Build(ds, new HashSet<string>(StringComparer.Ordinal));
        var inventoryId = Guid.NewGuid();

        if (drivePhases)
        {
            var drive = family.Drive("Record");
            await drive.ArrangeAsOwner(target, inventoryId, connStr);
            await drive.ActAsOther(target, inventoryId, connStr);
        }

        return new Run((ISkuToInventoryIdStore)target.Store, target.Tenant, inventoryId, ds);
    }
}
