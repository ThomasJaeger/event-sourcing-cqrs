using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class ReserveInventoryHandlerTests
{
    [Fact]
    public async Task HandleAsync_reserves_inventory_when_stock_is_available()
    {
        var fixture = new InventoryTestFixture();
        await fixture.SeedWithStockAsync();
        var handler = new ReserveInventoryHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new ReserveInventory(
                InventoryTestFixture.InventoryId,
                InventoryTestFixture.OrderId,
                InventoryTestFixture.LineId1,
                10),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Reserved.Should().Be(10);
        loaded.Available.Should().Be(90);
        loaded.Reservations.Should().ContainSingle();
        loaded.Reservations[0].LineId.Should().Be(InventoryTestFixture.LineId1);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_inventory_does_not_exist()
    {
        var fixture = new InventoryTestFixture();
        var handler = new ReserveInventoryHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new ReserveInventory(
                InventoryTestFixture.UnknownId,
                InventoryTestFixture.OrderId,
                InventoryTestFixture.LineId1,
                10),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(InventoryTestFixture.UnknownId);
    }
}
