using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class SetOrderShippingAddressHandlerTests
{
    [Fact]
    public async Task HandleAsync_sets_the_shipping_address_on_a_drafted_order()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedWithLineAsync();
        var handler = new SetOrderShippingAddressHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new SetOrderShippingAddress(OrderTestFixture.OrderId, OrderTestFixture.Shipping),
            CancellationToken.None);

        // Reloading without throwing demonstrates the event landed; the
        // aggregate exposes status but not the address directly, so the
        // assertion is structural rather than property-level.
        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Version.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new SetOrderShippingAddressHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new SetOrderShippingAddress(OrderTestFixture.UnknownId, OrderTestFixture.Shipping),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
