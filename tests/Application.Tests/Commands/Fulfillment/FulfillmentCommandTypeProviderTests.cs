using EventSourcingCqrs.Application.Commands.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Commands.Fulfillment;

public class FulfillmentCommandTypeProviderTests
{
    [Fact]
    public void GetCommandTypes_returns_the_five_Fulfillment_commands_in_canonical_order()
    {
        var provider = new FulfillmentCommandTypeProvider();

        provider.GetCommandTypes().Should().Equal(
            typeof(CreateInventory),
            typeof(AdjustInventory),
            typeof(DispatchShipment),
            typeof(DeliverShipment),
            typeof(ReturnShipment));
    }
}
