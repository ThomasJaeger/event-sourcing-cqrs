using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class PlaceOrderHandlerTests
{
    [Fact]
    public async Task HandleAsync_places_an_order_that_is_ready()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedReadyToPlaceAsync();
        var handler = new PlaceOrderHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(new PlaceOrder(OrderTestFixture.OrderId), CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(OrderStatus.Placed);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new PlaceOrderHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new PlaceOrder(OrderTestFixture.UnknownId),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
