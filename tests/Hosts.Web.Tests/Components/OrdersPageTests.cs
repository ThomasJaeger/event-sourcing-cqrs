using Bunit;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

public class OrdersPageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public OrdersPageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
    }

    [Fact]
    public void Orders_page_renders_table_header_with_expected_columns()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(
            new[] { SampleRow(Guid.NewGuid(), Guid.NewGuid()) });

        var cut = Render<Orders>();

        var headers = cut.FindAll("th").Select(th => th.TextContent.Trim()).ToList();
        headers.Should().Equal(
            "Order ID", "Customer ID", "Status", "Total",
            "Placed", "Last Updated", "Returned");
    }

    [Fact]
    public void Orders_page_renders_one_row_per_order_returned()
    {
        var rows = new List<OrderListRow>
        {
            SampleRow(Guid.NewGuid(), Guid.NewGuid()),
            SampleRow(Guid.NewGuid(), Guid.NewGuid()),
            SampleRow(Guid.NewGuid(), Guid.NewGuid()),
        };
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(rows);

        var cut = Render<Orders>();

        cut.FindAll("tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void Orders_page_renders_empty_state_when_no_orders()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(Array.Empty<OrderListRow>());

        var cut = Render<Orders>();

        cut.Markup.Should().Contain("No orders yet");
    }

    [Fact]
    public void Orders_page_queries_ListOrders_on_initialization()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(Array.Empty<OrderListRow>());

        Render<Orders>();

        stubApiClient.CapturedQueries.Should().HaveCount(1);
        stubApiClient.CapturedQueries[0].Should().BeOfType<ListOrders>();
    }

    [Fact]
    public void Orders_page_passes_default_offset_and_limit()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(Array.Empty<OrderListRow>());

        Render<Orders>();

        var captured = stubApiClient.CapturedQueries[0].Should().BeOfType<ListOrders>().Subject;
        captured.Offset.Should().Be(0);
        captured.Limit.Should().Be(50);
    }

    [Fact]
    public void Orders_page_renders_order_id_in_first_column()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(
            new[] { SampleRow(orderId, Guid.NewGuid()) });

        var cut = Render<Orders>();

        var firstCell = cut.Find("tbody tr td:nth-child(1)");
        firstCell.TextContent.Trim().Should().Be(orderId.ToString());
    }

    [Fact]
    public void Orders_page_renders_customer_id_in_second_column()
    {
        var customerId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(
            new[] { SampleRow(Guid.NewGuid(), customerId) });

        var cut = Render<Orders>();

        var secondCell = cut.Find("tbody tr td:nth-child(2)");
        secondCell.TextContent.Trim().Should().Be(customerId.ToString());
    }

    [Fact]
    public void Orders_page_renders_status_in_third_column()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(
            new[] { SampleRow(Guid.NewGuid(), Guid.NewGuid(), status: OrderStatus.Placed) });

        var cut = Render<Orders>();

        var thirdCell = cut.Find("tbody tr td:nth-child(3)");
        thirdCell.TextContent.Trim().Should().Be("Placed");
    }

    [Fact]
    public void Orders_page_renders_returned_indicator_in_last_column()
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(
            new[] { SampleRow(Guid.NewGuid(), Guid.NewGuid(), isReturned: true) });

        var cut = Render<Orders>();

        var lastCell = cut.Find("tbody tr td:nth-child(7)");
        lastCell.TextContent.Trim().Should().Be("Yes");
    }

    private static OrderListRow SampleRow(
        Guid orderId,
        Guid customerId,
        OrderStatus status = OrderStatus.Draft,
        bool isReturned = false)
    {
        return new OrderListRow(
            OrderId: orderId,
            CustomerId: customerId,
            Status: status,
            Total: new Money(100m, Currency.USD),
            PlacedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            IsReturned: isReturned,
            ReturnedUtc: isReturned ? new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc) : null);
    }
}
