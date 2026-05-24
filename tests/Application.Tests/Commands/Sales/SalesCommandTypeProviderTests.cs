using EventSourcingCqrs.Application.Commands.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Sales;

public class SalesCommandTypeProviderTests
{
    [Fact]
    public void GetCommandTypes_returns_the_eight_Sales_commands_in_canonical_order()
    {
        var provider = new SalesCommandTypeProvider();

        provider.GetCommandTypes().Should().Equal(
            typeof(DraftOrder),
            typeof(AddOrderLine),
            typeof(RemoveOrderLine),
            typeof(SetOrderShippingAddress),
            typeof(PlaceOrder),
            typeof(ShipOrder),
            typeof(CancelOrder),
            typeof(MarkOrderCompleted));
    }
}
