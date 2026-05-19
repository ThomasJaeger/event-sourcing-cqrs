using EventSourcingCqrs.Application.Commands.Fulfillment;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public sealed class ScheduleShipmentHandlerTests
{
    [Fact]
    public async Task HandleAsync_schedules_a_new_shipment_and_persists_it()
    {
        var fixture = new ShipmentTestFixture();
        var handler = new ScheduleShipmentHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new ScheduleShipment(
                ShipmentTestFixture.ShipmentId,
                ShipmentTestFixture.OrderId,
                ShipmentTestFixture.Destination,
                ShipmentTestFixture.Lines),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(ShipmentTestFixture.ShipmentId);
        loaded.OrderId.Should().Be(ShipmentTestFixture.OrderId);
        loaded.Status.Should().Be(ShipmentStatus.Scheduled);
        loaded.Lines.Should().HaveCount(1);
    }
}
