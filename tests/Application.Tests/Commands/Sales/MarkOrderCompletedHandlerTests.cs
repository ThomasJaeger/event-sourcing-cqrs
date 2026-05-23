using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class MarkOrderCompletedHandlerTests
{
    [Fact]
    public async Task HandleAsync_completes_a_placed_order()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedPlacedAsync();
        var handler = new MarkOrderCompletedHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new MarkOrderCompleted(OrderTestFixture.OrderId),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(OrderStatus.Completed);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new MarkOrderCompletedHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new MarkOrderCompleted(OrderTestFixture.UnknownId),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
