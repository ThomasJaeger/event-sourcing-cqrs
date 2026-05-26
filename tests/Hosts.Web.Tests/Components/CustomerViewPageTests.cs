using Bunit;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

public class CustomerViewPageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public CustomerViewPageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
    }

    [Fact]
    public void CustomerView_page_renders_no_activity_when_query_returns_null()
    {
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(null);

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, Guid.NewGuid()));

        cut.Markup.Should().Contain("No order activity for this customer");
    }

    [Fact]
    public void CustomerView_page_renders_header_with_customer_id()
    {
        var customerId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(customerId));

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, customerId));

        cut.Find("h1").TextContent.Should().Contain(customerId.ToString());
    }

    [Fact]
    public void CustomerView_page_renders_order_count()
    {
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(Guid.NewGuid(), orderCount: 7));

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, Guid.NewGuid()));

        cut.Find("p.font-semibold").TextContent.Trim().Should().Be("7");
    }

    [Fact]
    public void CustomerView_page_renders_lifetime_value()
    {
        var lifetimeValue = new Money(250m, Currency.USD);
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(Guid.NewGuid(), lifetimeValue: lifetimeValue));

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, Guid.NewGuid()));

        cut.Markup.Should().Contain(lifetimeValue.ToString());
    }

    [Fact]
    public void CustomerView_page_renders_last_order_date()
    {
        var lastOrderUtc = new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc);
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(Guid.NewGuid(), lastOrderUtc: lastOrderUtc));

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, Guid.NewGuid()));

        cut.Markup.Should().Contain(lastOrderUtc.ToString("u"));
    }

    [Fact]
    public void CustomerView_page_renders_last_updated_date()
    {
        var lastUpdatedUtc = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(Guid.NewGuid(), lastUpdatedUtc: lastUpdatedUtc));

        var cut = Render<CustomerView>(p => p.Add(x => x.CustomerId, Guid.NewGuid()));

        cut.Markup.Should().Contain(lastUpdatedUtc.ToString("u"));
    }

    [Fact]
    public void CustomerView_page_queries_GetCustomerSummary_with_route_CustomerId()
    {
        var customerId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetCustomerSummary, CustomerSummaryRow?>(
            SampleSummary(customerId));

        Render<CustomerView>(p => p.Add(x => x.CustomerId, customerId));

        var captured = stubApiClient.CapturedQueries.Should().ContainSingle().Subject
            .Should().BeOfType<GetCustomerSummary>().Subject;
        captured.CustomerId.Should().Be(customerId);
    }

    private static CustomerSummaryRow SampleSummary(
        Guid customerId,
        int orderCount = 3,
        Money? lifetimeValue = null,
        DateTime? lastOrderUtc = null,
        DateTime? lastUpdatedUtc = null)
    {
        return new CustomerSummaryRow(
            CustomerId: customerId,
            OrderCount: orderCount,
            LifetimeValue: lifetimeValue ?? new Money(100m, Currency.USD),
            LastOrderUtc: lastOrderUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LastUpdatedUtc: lastUpdatedUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    }
}
