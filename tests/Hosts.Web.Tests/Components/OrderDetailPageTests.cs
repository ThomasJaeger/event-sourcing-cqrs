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

public class OrderDetailPageTests : BunitContext
{
    private readonly StubApiClient stubApiClient = new();

    public OrderDetailPageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        // The page injects TimeProvider for its polling loop. These render-only
        // tests never start polling, so the system clock satisfies the dependency.
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void OrderDetail_page_renders_not_found_when_query_returns_null()
    {
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(null);

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Markup.Should().Contain("Order not found");
    }

    [Fact]
    public void OrderDetail_page_renders_header_with_order_id()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(
            SampleDetail(orderId, OrderStatus.Placed));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        cut.Find("h1").TextContent.Should().Contain(orderId.ToString());
    }

    [Fact]
    public void OrderDetail_page_renders_CancelOrderButton_when_status_is_Placed()
    {
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(
            SampleDetail(Guid.NewGuid(), OrderStatus.Placed));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Markup.Should().Contain("Cancel Order");
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Completed)]
    public void OrderDetail_page_hides_CancelOrderButton_for_non_Placed_statuses(OrderStatus status)
    {
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(
            SampleDetail(Guid.NewGuid(), status));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Markup.Should().NotContain("Cancel Order");
    }

    [Fact]
    public void OrderDetail_page_renders_line_items_when_present()
    {
        var detail = SampleDetail(Guid.NewGuid(), OrderStatus.Placed, lineCount: 3);
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(detail);

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.FindAll("tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void OrderDetail_page_renders_empty_line_items_state()
    {
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(
            SampleDetail(Guid.NewGuid(), OrderStatus.Placed, lineCount: 0));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.Markup.Should().Contain("No line items");
    }

    [Fact]
    public void OrderDetail_page_renders_timeline_entries()
    {
        var detail = SampleDetail(Guid.NewGuid(), OrderStatus.Placed, timelineCount: 4);
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(detail);

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, Guid.NewGuid()));

        cut.FindAll("ul li").Should().HaveCount(4);
    }

    [Fact]
    public void OrderDetail_page_queries_GetOrderDetail_with_route_OrderId()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(
            SampleDetail(orderId, OrderStatus.Placed));

        Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        var captured = stubApiClient.CapturedQueries.Should().ContainSingle().Subject
            .Should().BeOfType<GetOrderDetail>().Subject;
        captured.OrderId.Should().Be(orderId);
    }

    private static OrderDetailView SampleDetail(
        Guid orderId,
        OrderStatus status,
        int lineCount = 1,
        int timelineCount = 1)
    {
        var header = new OrderDetailRow(
            OrderId: orderId,
            CustomerId: Guid.NewGuid(),
            Status: status,
            PlacedUtc: status >= OrderStatus.Placed ? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) : null,
            ShippedUtc: null,
            CancelledUtc: status == OrderStatus.Cancelled ? new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc) : null,
            CompletedUtc: null,
            ReturnedUtc: null,
            Total: new Money(100m, Currency.USD),
            ShippingAddress: null,
            LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        var lines = Enumerable.Range(0, lineCount)
            .Select(i => new OrderDetailLineRow(
                OrderId: orderId,
                LineId: Guid.NewGuid(),
                Sku: $"SKU-{i}",
                Quantity: 1,
                UnitPrice: new Money(50m, Currency.USD)))
            .ToList();

        var timeline = Enumerable.Range(0, timelineCount)
            .Select(i => new OrderDetailTimelineRow(
                OrderId: orderId,
                GlobalPosition: i,
                EventType: $"Event{i}",
                OccurredUtc: new DateTime(2026, 1, 1, 12, i, 0, DateTimeKind.Utc),
                Payload: "{}"))
            .ToList();

        return new OrderDetailView(header, lines, timeline);
    }
}
