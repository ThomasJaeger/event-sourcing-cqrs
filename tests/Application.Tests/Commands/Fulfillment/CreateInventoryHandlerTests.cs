using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class CreateInventoryHandlerTests
{
    [Fact]
    public async Task HandleAsync_creates_a_new_inventory_and_persists_it()
    {
        var fixture = new InventoryTestFixture();
        var handler = new CreateInventoryHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new CreateInventory(InventoryTestFixture.InventoryId, InventoryTestFixture.Sku),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(InventoryTestFixture.InventoryId);
        loaded.Sku.Should().Be(InventoryTestFixture.Sku);
    }
}
