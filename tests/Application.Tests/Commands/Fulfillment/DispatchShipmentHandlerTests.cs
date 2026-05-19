using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class DispatchShipmentHandlerTests
{
    [Fact]
    public async Task HandleAsync_dispatches_a_scheduled_shipment()
    {
        var fixture = new ShipmentTestFixture();
        await fixture.SeedScheduledAsync();
        var handler = new DispatchShipmentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new DispatchShipment(
                ShipmentTestFixture.ShipmentId,
                ShipmentTestFixture.CarrierReference),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(ShipmentStatus.Dispatched);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_shipment_does_not_exist()
    {
        var fixture = new ShipmentTestFixture();
        var handler = new DispatchShipmentHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new DispatchShipment(
                ShipmentTestFixture.UnknownId,
                ShipmentTestFixture.CarrierReference),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(ShipmentTestFixture.UnknownId);
    }
}
