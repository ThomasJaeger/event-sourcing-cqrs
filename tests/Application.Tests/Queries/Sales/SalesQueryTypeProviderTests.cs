using EventSourcingCqrs.Application.Queries.Sales;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Application.Tests.Queries.Sales;

public class SalesQueryTypeProviderTests
{
    [Fact]
    public void GetQueryTypes_returns_the_three_Sales_queries_in_canonical_order()
    {
        var provider = new SalesQueryTypeProvider();

        provider.GetQueryTypes().Should().Equal(
            typeof(ListOrders),
            typeof(GetOrderDetail),
            typeof(GetCustomerSummary));
    }
}
