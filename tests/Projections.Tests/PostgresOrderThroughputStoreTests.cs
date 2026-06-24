using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.Projections.Tests;

public class PostgresOrderThroughputStoreTests : IClassFixture<PostgresFixture>
{
    private const string ProjectionName = "order-throughput";
    private static readonly DateTime SecondOld = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondNew = new(2026, 5, 14, 9, 5, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantOne = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TenantId TenantTwo = TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly PostgresFixture _fixture;

    public PostgresOrderThroughputStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Increment_and_prune_persist_per_tenant_buckets_isolated_from_other_tenants()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);

        var store = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = TenantOne });

        await using (var uow = await store.BeginAsync(CancellationToken.None))
        {
            // The older second is counted once; the newer second twice.
            await uow.IncrementSecondAsync(SecondOld, CancellationToken.None);
            await uow.IncrementSecondAsync(SecondNew, CancellationToken.None);
            await uow.IncrementSecondAsync(SecondNew, CancellationToken.None);
            // Prune everything strictly older than the newer second: the older bucket goes.
            await uow.PruneBeforeAsync(SecondNew, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        var buckets = await store.GetBucketsAsync(CancellationToken.None);

        // The twice-incremented second persists with count 2; the older second was pruned.
        buckets.Should().ContainSingle();
        var bucket = buckets.Single();
        bucket.SecondUtc.Should().Be(SecondNew);
        bucket.Count.Should().Be(2);

        // Tenant isolation: a different tenant reading the same database sees none of
        // TenantOne's rows.
        var otherTenantStore = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = TenantTwo });
        (await otherTenantStore.GetBucketsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Resetting_one_tenant_clears_only_that_tenants_buckets()
    {
        var connStr = await _fixture.CreateMigratedDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(connStr);
        var factory = new NpgsqlReadModelConnectionFactory(dataSource);

        var tenantOneStore = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = TenantOne });
        var tenantTwoStore = new PostgresOrderThroughputStore(
            factory, new PostgresCheckpointStore(factory), TestNotificationPublisher.Create(),
            new StubTenantAccessor { Current = TenantTwo });

        await using (var uow = await tenantOneStore.BeginAsync(CancellationToken.None))
        {
            await uow.IncrementSecondAsync(SecondNew, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }
        await using (var uow = await tenantTwoStore.BeginAsync(CancellationToken.None))
        {
            await uow.IncrementSecondAsync(SecondNew, CancellationToken.None);
            await uow.CommitAsync(ProjectionName, 1, CancellationToken.None);
        }

        // Reset only TenantOne through the ITenantResettable surface the store does not implement yet.
        await ((ITenantResettable)tenantOneStore).ResetTenantAsync(TenantOne, CancellationToken.None);

        // TenantOne's buckets are gone; TenantTwo's remain, so the reset is a tenant-scoped delete,
        // not a whole-table truncate.
        (await tenantOneStore.GetBucketsAsync(CancellationToken.None)).Should().BeEmpty();
        (await tenantTwoStore.GetBucketsAsync(CancellationToken.None)).Should().ContainSingle();
    }
}
