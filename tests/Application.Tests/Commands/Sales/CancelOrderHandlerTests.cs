using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class CancelOrderHandlerTests
{
    private static readonly Guid IssuedBy = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task HandleAsync_cancels_a_drafted_order()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedDraftedAsync();
        var handler = new CancelOrderHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new CancelOrder(OrderTestFixture.OrderId, "Customer changed mind", IssuedBy),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new CancelOrderHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new CancelOrder(OrderTestFixture.UnknownId, "n/a", IssuedBy),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
