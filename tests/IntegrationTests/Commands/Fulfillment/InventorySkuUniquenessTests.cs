using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Access;
using EventSourcingCqrs.Domain.Access.Events;
using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Infrastructure.EventStore.Postgres;
using EventSourcingCqrs.Infrastructure.Versioning;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

public sealed class InventorySkuUniquenessTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Creating_inventory_after_access_bootstrap_enforces_sku_uniqueness_before_append()
    {
        // Workers writes Access events during bootstrap. Seed through a separately composed
        // real store so this fact requires the API's own read registry to understand them.
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var registry = new EventTypeRegistry().Register<RoleAssigned>();
        var seedStore = new PostgresEventStore(
            new NpgsqlConnectionFactory(dataSource), registry, new ProcessManagerEventTypeRegistry(),
            EventStoreJsonOptions.Create(), new EventUpcasterPipeline(registry, []));
        var actorId = Guid.NewGuid();
        await EventStoreSeed.AppendAsync(seedStore,
            StreamId.ForAggregate<UserRoles>(WellKnownTenants.Default, actorId),
            [new RoleAssigned(actorId, Role.Admin)]);
        using var client = fixture.Factory.CreateClient();
        var owner = Guid.NewGuid();
        var rejected = Guid.NewGuid();
        var sku = "SKU-" + Guid.NewGuid().ToString("N");

        var first = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId = owner, sku }, Guid.NewGuid().ToString());
        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var second = await client.PostCommandAsync(
            "CreateInventory", new { inventoryId = rejected, sku }, Guid.NewGuid().ToString());

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var store = fixture.Factory.Services.GetRequiredService<IEventStore>();
        (await store.ReadStreamAsync(StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, rejected)))
            .Should().BeEmpty();
    }
}
