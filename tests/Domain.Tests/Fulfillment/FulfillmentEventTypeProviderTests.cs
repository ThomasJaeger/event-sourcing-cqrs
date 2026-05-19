using EventSourcingCqrs.Domain.Fulfillment;
using EventSourcingCqrs.Domain.Fulfillment.Events;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Domain.Tests.Fulfillment;

public class FulfillmentEventTypeProviderTests
{
    [Fact]
    public void GetEventTypes_returns_all_Fulfillment_events_in_canonical_lifecycle_order()
    {
        var provider = new FulfillmentEventTypeProvider();

        provider.GetEventTypes().Should().Equal(
            typeof(InventoryCreated),
            typeof(InventoryAdjusted),
            typeof(InventoryReserved),
            typeof(InventoryReleased),
            typeof(ShipmentScheduled),
            typeof(ShipmentDispatched),
            typeof(ShipmentDelivered),
            typeof(ShipmentReturned));
    }
}
