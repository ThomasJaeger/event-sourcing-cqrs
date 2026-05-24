using EventSourcingCqrs.Application.Queries.Fulfillment;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Fulfillment;

public class FulfillmentQueryTypeProviderTests
{
    [Fact]
    public void GetQueryTypes_returns_the_two_Fulfillment_queries_in_canonical_order()
    {
        var provider = new FulfillmentQueryTypeProvider();

        provider.GetQueryTypes().Should().Equal(
            typeof(GetAllInventoryDashboard),
            typeof(GetInventoryDashboardBySku));
    }
}
