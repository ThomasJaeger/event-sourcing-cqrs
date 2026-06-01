using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

public class CreateInventoryTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public CreateInventoryTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_create_inventory_appends_inventory_created_event()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();

        var response = await client.PostCommandAsync(
            "CreateInventory",
            new { inventoryId, sku = "SKU-NEW" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, inventoryId), fromVersion: 0);
        envelopes.Should().ContainSingle();
        envelopes[0].EventType.Should().Be("InventoryCreated");
    }
}
