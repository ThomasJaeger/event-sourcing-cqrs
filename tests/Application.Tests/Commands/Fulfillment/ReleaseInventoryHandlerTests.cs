using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class ReleaseInventoryHandlerTests
{
    [Fact]
    public async Task HandleAsync_releases_an_existing_reservation()
    {
        var fixture = new InventoryTestFixture();
        await fixture.SeedWithReservationAsync();
        var handler = new ReleaseInventoryHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new ReleaseInventory(
                InventoryTestFixture.InventoryId,
                InventoryTestFixture.LineId1,
                "Order cancelled"),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Reserved.Should().Be(0);
        loaded.Available.Should().Be(100);
        loaded.Reservations.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_inventory_does_not_exist()
    {
        var fixture = new InventoryTestFixture();
        var handler = new ReleaseInventoryHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new ReleaseInventory(
                InventoryTestFixture.UnknownId,
                InventoryTestFixture.LineId1,
                "Order cancelled"),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(InventoryTestFixture.UnknownId);
    }
}
