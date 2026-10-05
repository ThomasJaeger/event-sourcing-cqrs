using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.ReadModels.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using EventSourcingCqrs.TestInfrastructure;
using FluentAssertions;
using Npgsql;
using Xunit;
using static EventSourcingCqrs.Infrastructure.Tests.Postgres.PostgresEventStoreTestKit;

namespace EventSourcingCqrs.Infrastructure.Tests.Postgres;

public sealed class PostgresInventorySkuRegistryTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Concurrent_claims_from_separate_instances_have_exactly_one_owner()
    {
        await using var harness = await CreateAsync();
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            var id = Guid.NewGuid();
            try
            {
                await harness.Registry().ClaimAsync(WellKnownTenants.Default, id, "SKU", CancellationToken.None);
                return (Guid?)id;
            }
            catch (DomainException)
            {
                return null;
            }
        });

        var owners = (await Task.WhenAll(attempts)).Where(id => id.HasValue).ToList();

        owners.Should().ContainSingle();
        await harness.Registry().ClaimAsync(
            WellKnownTenants.Default, owners.Single()!.Value, "SKU", CancellationToken.None);
    }

    [Fact]
    public async Task A_claim_survives_without_an_append_and_allows_only_the_same_mapping_to_retry()
    {
        await using var harness = await CreateAsync();
        var tenant = WellKnownTenants.Default;
        var id = Guid.NewGuid();
        await harness.Registry().ClaimAsync(tenant, id, "SKU", CancellationToken.None);

        await harness.Registry().ClaimAsync(tenant, id, "SKU", CancellationToken.None);
        var rival = () => harness.Registry().ClaimAsync(tenant, Guid.NewGuid(), "SKU", CancellationToken.None);
        var changedSku = () => harness.Registry().ClaimAsync(tenant, id, "OTHER", CancellationToken.None);

        await rival.Should().ThrowAsync<DomainException>();
        await changedSku.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Historical_inventory_is_claimed_before_a_new_create_without_waiting_for_projections()
    {
        await using var harness = await CreateAsync();
        var tenant = WellKnownTenants.Default;
        var historicId = Guid.NewGuid();
        await harness.SeedAsync(tenant, historicId, "SKU");

        var conflictingCreate = () => harness.Registry().ClaimAsync(
            tenant, Guid.NewGuid(), "SKU", CancellationToken.None);

        await conflictingCreate.Should().ThrowAsync<DomainException>();
        await harness.Registry().ClaimAsync(tenant, historicId, "SKU", CancellationToken.None);
    }

    [Fact]
    public async Task Conflicting_historical_inventory_blocks_initialization_and_rolls_back_the_backfill()
    {
        await using var harness = await CreateAsync();
        var tenant = WellKnownTenants.Default;
        await harness.SeedAsync(tenant, Guid.NewGuid(), "DUPLICATE");
        await harness.SeedAsync(tenant, Guid.NewGuid(), "DUPLICATE");

        var act = () => harness.Registry().ClaimAsync(tenant, Guid.NewGuid(), "NEW", CancellationToken.None);

        await act.Should().ThrowAsync<InventorySkuRegistryInitializationException>();
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM write_side.inventory_sku_claims";
        ((long)(await count.ExecuteScalarAsync())!).Should().Be(0);
    }

    [Fact]
    public async Task The_same_sku_and_inventory_id_can_be_claimed_independently_by_two_tenants()
    {
        await using var harness = await CreateAsync();
        var id = Guid.NewGuid();

        await harness.Registry().ClaimAsync(WellKnownTenants.Default, id, "SKU", CancellationToken.None);
        await harness.Registry().ClaimAsync(TenantId.From(Guid.NewGuid()), id, "SKU", CancellationToken.None);
    }

    private async Task<Harness> CreateAsync()
        => new(NpgsqlDataSource.Create(await fixture.CreateMigratedDatabaseAsync()));

    private sealed class Harness : IAsyncDisposable
    {
        private readonly PostgresEventStore _events;
        private readonly NpgsqlReadModelConnectionFactory _readFactory;
        public NpgsqlDataSource DataSource { get; }

        public Harness(NpgsqlDataSource dataSource)
        {
            DataSource = dataSource;
            _readFactory = new NpgsqlReadModelConnectionFactory(dataSource);
            var registry = new EventTypeRegistry().Register<InventoryCreated>();
            _events = new PostgresEventStore(
                new NpgsqlConnectionFactory(dataSource), registry, new ProcessManagerEventTypeRegistry(),
                EventStoreJsonOptions.Create(), new EventUpcasterPipeline(registry, []));
        }

        public PostgresInventorySkuRegistry Registry() => new(_readFactory, _events);

        public Task SeedAsync(TenantId tenant, Guid inventoryId, string sku)
        {
            var stream = StreamId.ForAggregate<Inventory>(tenant, inventoryId);
            return _events.AppendAsync(stream, 0,
                [BuildEnvelope(stream, 1, new InventoryCreated(inventoryId, sku, DateTime.UtcNow), tenant: tenant)],
                CancellationToken.None);
        }

        public ValueTask DisposeAsync() => DataSource.DisposeAsync();
    }
}
