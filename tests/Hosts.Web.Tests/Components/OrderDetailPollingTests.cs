using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// Drives OrderDetail's StartPolling loop with a FakeTimeProvider so the loop's
// production behavior gets coverage (the loop reads the clock and delays through
// TimeProvider as of Commit 24a). Polling fires off the CancelOrderButton's
// OnDispatched callback, so each test renders the page on a Placed order, clicks
// Cancel, then advances fake time to exercise one of the three loop exits: settled
// (read model flips to Cancelled), deadline (the 30s window elapses with no flip),
// and cancellation (the page disposes while the loop is parked on its delay).
public sealed class OrderDetailPollingTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);

    public OrderDetailPollingTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
    }

    [Fact]
    public async Task Polling_settles_when_read_model_flips_to_Cancelled()
    {
        var orderId = Guid.NewGuid();
        // First query: the initial render shows the order Placed, so the Cancel
        // button renders. Second query: the poll's first load sees it Cancelled,
        // which settles the button and re-renders the page without it.
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Cancelled));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();

        // The loop is parked on Task.Delay(1s, fakeTime). Advancing past it on the
        // renderer dispatcher resumes the loop, which loads the Cancelled view.
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(1)));

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Cancel Order"));
        stubApiClient.CapturedQueries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Polling_times_out_when_the_deadline_elapses_without_settlement()
    {
        var orderId = Guid.NewGuid();
        // The order stays Placed across the initial render and the single poll load
        // that runs before the deadline guard exits the loop.
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();

        // One advance past the 30s deadline: the parked delay completes, the loop
        // loads once (still Placed), then the while guard sees the clock past the
        // deadline and exits to MarkPollingTimeout.
        await cut.InvokeAsync(() => fakeTime.Advance(TimeSpan.FromSeconds(31)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    [Fact]
    public async Task Polling_stops_loading_when_the_page_is_disposed_mid_poll()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();

        // The loop is parked on the 1s delay, having issued only the initial query.
        stubApiClient.CapturedQueries.Should().HaveCount(1);

        // Disposing the page cancels pollingCts in DisposeAsync; the parked delay
        // throws OperationCanceledException and the loop returns before loading.
        await DisposeComponentsAsync();

        stubApiClient.CapturedQueries.Should().HaveCount(1);
    }

    private static OrderDetailView SampleDetail(Guid orderId, OrderStatus status) =>
        new(
            new OrderDetailRow(
                OrderId: orderId,
                CustomerId: Guid.NewGuid(),
                Status: status,
                PlacedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                ShippedUtc: null,
                CancelledUtc: status == OrderStatus.Cancelled
                    ? new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)
                    : null,
                CompletedUtc: null,
                ReturnedUtc: null,
                Total: new Money(100m, Currency.USD),
                ShippingAddress: null,
                LastUpdatedUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)),
            [],
            []);
}
