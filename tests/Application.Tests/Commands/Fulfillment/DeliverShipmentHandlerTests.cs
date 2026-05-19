using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class DeliverShipmentHandlerTests
{
    [Fact]
    public async Task HandleAsync_delivers_a_dispatched_shipment()
    {
        var fixture = new ShipmentTestFixture();
        await fixture.SeedDispatchedAsync();
        var handler = new DeliverShipmentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new DeliverShipment(ShipmentTestFixture.ShipmentId),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(ShipmentStatus.Delivered);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_shipment_does_not_exist()
    {
        var fixture = new ShipmentTestFixture();
        var handler = new DeliverShipmentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new DeliverShipment(ShipmentTestFixture.UnknownId),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(ShipmentTestFixture.UnknownId);
    }
}
