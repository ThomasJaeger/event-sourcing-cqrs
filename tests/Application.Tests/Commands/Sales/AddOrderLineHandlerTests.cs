using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Tests.TestKit;
using EventSourcingCqrs.Domain.Abstractions;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public sealed class AddOrderLineHandlerTests
{
    [Fact]
    public async Task HandleAsync_appends_a_line_to_a_drafted_order()
    {
        var fixture = new OrderTestFixture();
        await fixture.SeedDraftedAsync();
        var handler = new AddOrderLineHandler(fixture.Repository, fixture.Accessor);

        await handler.HandleAsync(
            new AddOrderLine(
                OrderTestFixture.OrderId,
                OrderTestFixture.LineId1,
                "SKU-1",
                2,
                OrderTestFixture.TenUsd),
            CancellationToken.None);

        var loaded = await fixture.LoadAsync();
        loaded!.Lines.Should().ContainSingle();
        loaded.Lines[0].LineId.Should().Be(OrderTestFixture.LineId1);
        loaded.Lines[0].Sku.Should().Be("SKU-1");
    }

    [Fact]
    public async Task HandleAsync_throws_AggregateNotFoundException_when_the_order_does_not_exist()
    {
        var fixture = new OrderTestFixture();
        var handler = new AddOrderLineHandler(fixture.Repository, fixture.Accessor);

        var act = () => handler.HandleAsync(
            new AddOrderLine(
                OrderTestFixture.UnknownId,
                OrderTestFixture.LineId1,
                "SKU-1",
                2,
                OrderTestFixture.TenUsd),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateNotFoundException>()).Which;
        thrown.AggregateId.Should().Be(OrderTestFixture.UnknownId);
    }
}
