using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Sales;

public class SalesEventTypeProviderTests
{
    [Fact]
    public void GetEventTypes_returns_the_seven_Order_events_in_canonical_order()
    {
        var provider = new SalesEventTypeProvider();

        provider.GetEventTypes().Should().Equal(
            typeof(OrderDrafted),
            typeof(OrderLineAdded),
            typeof(OrderLineRemoved),
            typeof(ShippingAddressSet),
            typeof(OrderPlaced),
            typeof(OrderShipped),
            typeof(OrderCancelled));
    }
}
