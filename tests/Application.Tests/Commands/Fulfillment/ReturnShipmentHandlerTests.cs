using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class ReturnShipmentHandlerTests
{
    [Fact]
    public async Task HandleAsync_returns_a_delivered_shipment()
    {
        var fixture = new ShipmentTestFixture();
        await fixture.SeedDeliveredAsync();
        var handler = new ReturnShipmentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new ReturnShipment(ShipmentTestFixture.ShipmentId, "Customer reported damage"),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(ShipmentStatus.Returned);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_shipment_does_not_exist()
    {
        var fixture = new ShipmentTestFixture();
        var handler = new ReturnShipmentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new ReturnShipment(ShipmentTestFixture.UnknownId, "Customer reported damage"),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(ShipmentTestFixture.UnknownId);
    }
}
