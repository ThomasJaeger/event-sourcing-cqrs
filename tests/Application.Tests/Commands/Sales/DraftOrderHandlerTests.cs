using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class DraftOrderHandlerTests
{
    [Fact]
    public async Task HandleAsync_drafts_a_new_order_and_persists_it()
    {
        var fixture = new OrderTestFixture();
        var handler = new DraftOrderHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new DraftOrder(OrderTestFixture.OrderId, OrderTestFixture.CustomerId),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(OrderTestFixture.OrderId);
        loaded.Status.Should().Be(OrderStatus.Draft);
    }
}
