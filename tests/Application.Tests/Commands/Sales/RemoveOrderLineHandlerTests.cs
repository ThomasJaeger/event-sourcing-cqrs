using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class RemoveOrderLineHandlerTests
{
    [Fact]
    public async Task HandleAsync_removes_a_line_from_a_drafted_order()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedWithLineAsync();
        var handler = new RemoveOrderLineHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new RemoveOrderLine(OrderTestFixture.OrderId, OrderTestFixture.LineId1),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new RemoveOrderLineHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new RemoveOrderLine(OrderTestFixture.UnknownId, OrderTestFixture.LineId1),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
