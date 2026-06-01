using System.Net;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.IntegrationTests.Commands.Fulfillment;

public class AdjustInventoryTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public AdjustInventoryTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Posting_adjust_inventory_appends_inventory_adjusted_event()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();

        await InventorySetup.CreateAsync(client, inventoryId);

        var response = await client.PostCommandAsync(
            "AdjustInventory",
            new { inventoryId, quantityDelta = 10, reason = "initial stock" },
            idempotencyKey: Guid.NewGuid().ToString());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var eventStore = _fixture.Factory.Services.GetRequiredService<IEventStore>();
        var envelopes = await eventStore.ReadStreamAsync(
            StreamId.ForAggregate<Inventory>(WellKnownTenants.Default, inventoryId), fromVersion: 0);
        envelopes.Should().HaveCount(2);
        envelopes[1].EventType.Should().Be("InventoryAdjusted");
    }

    [Fact]
    public async Task Posting_adjust_inventory_that_pushes_available_negative_returns_422()
    {
        var client = _fixture.Factory.CreateClient();
        var inventoryId = Guid.NewGuid();

        await InventorySetup.CreateAsync(client, inventoryId);

        var response = await client.PostCommandAsync(
            "AdjustInventory",
            new { inventoryId, quantityDelta = -5, reason = "shrinkage" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Posting_adjust_inventory_on_unknown_inventory_returns_404()
    {
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostCommandAsync(
            "AdjustInventory",
            new { inventoryId = Guid.NewGuid(), quantityDelta = 10, reason = "any" },
            idempotencyKey: Guid.NewGuid().ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
