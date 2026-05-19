using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class AdjustInventoryHandlerTests
{
    [Fact]
    public async Task HandleAsync_adjusts_the_inventory_when_it_exists()
    {
        var fixture = new InventoryTestFixture();
        await fixture.SeedCreatedAsync();
        var handler = new AdjustInventoryHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new AdjustInventory(InventoryTestFixture.InventoryId, 50, "Initial stock"),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.TotalAdjusted.Should().Be(50);
        loaded.Available.Should().Be(50);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_inventory_does_not_exist()
    {
        var fixture = new InventoryTestFixture();
        var handler = new AdjustInventoryHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new AdjustInventory(InventoryTestFixture.UnknownId, 50, "Initial stock"),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(InventoryTestFixture.UnknownId);
    }
}
