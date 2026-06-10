using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Commands.Sales;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// Commit 2 (ADR 0032), resolution c: the push retrofit of OrderDetail. Replaces the deleted
// OrderDetailPollingTests. The page subscribes through the in-process circuit subscription on first
// interactive render, re-queries authoritative state on each notification (marshalled), and arms a
// degraded-settlement timer on cancel-dispatch. These specs are RED until GREEN wires the page to the
// injected subscription and the timer; the page scaffold injects but does not use them.
public sealed class OrderDetailPushTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);
    private readonly StubCircuitResourceSubscription subscription = new();

    public OrderDetailPushTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
        Services.AddSingleton<ICircuitResourceSubscription>(subscription);
    }

    // D: the page subscribes on first interactive render. (The late-snapshot-vs-notification clobber
    // guard is the subscription's own invariant, proven in CircuitResourceSubscriptionTests; here we pin
    // that the page engages the subscription on render.)
    [Fact]
    public void Page_subscribes_on_first_interactive_render_for_its_order()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));

        Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        subscription.StartCallCount.Should().Be(1);
        subscription.LastResourceType.Should().Be(SubscriptionResourceType.Order);
    }

    // Invariant (arm-throw): when StartAsync faults at the arm with a non-OperationCanceledException, the render does not throw, the subscription is left in place (not disposed), and the page keeps the initial order it loaded in OnInitializedAsync.
    [Fact]
    public void An_arm_failure_at_render_is_caught_and_leaves_the_page_on_its_initial_data()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        subscription.ThrowFromStart(new ApiInfrastructureException("subscription arm failed", statusCode: 503));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();
        cut.Find("h1").TextContent.Should().Contain(orderId.ToString());
    }

    // Invariant (arm-cancel): pins the OperationCanceledException catch arm so a cancellation faulting StartAsync cannot escape and fault the circuit; the page stays on its initial data with the subscription in place.
    [Fact]
    public void A_cancellation_during_the_arm_is_caught_and_leaves_the_page_on_its_initial_data()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        subscription.ThrowFromStart(new OperationCanceledException());

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();
        cut.Find("h1").TextContent.Should().Contain(orderId.ToString());
    }

    // P11.9 (green on write): the OCE arm is teardown, not failure; it must not surface the NotLive badge.
    [Fact]
    public void A_cancellation_during_the_arm_does_not_surface_the_not_live_badge()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        subscription.ThrowFromStart(new OperationCanceledException());

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        cut.Markup.Should().NotContain("Live updates unavailable");
    }

    // P11.9: a successful arm surfaces the Live badge inside the loaded detail region.
    [Fact]
    public void A_successful_arm_at_render_surfaces_the_live_badge()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    // P11.9: the arm failure that previously left the page silently not live now surfaces the NotLive badge.
    [Fact]
    public void An_arm_failure_at_render_surfaces_the_not_live_badge()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        subscription.ThrowFromStart(new ApiInfrastructureException("subscription arm failed", statusCode: 503));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable; reload to refresh.");
    }

    // G (blocker 7): the page subscribes under OrderId.ToString() (default "D"), byte-equal to
    // OrderDetailProjection's envelope ResourceId (orderId.ToString()); ResourceKey equality is ordinal,
    // so the "N" form would not route.
    [Fact]
    public void Page_subscribes_under_the_default_D_form_order_id_matching_the_projection_envelope()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));

        Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        subscription.LastResourceId.Should().Be(orderId.ToString());
        subscription.LastResourceId.Should().NotBe(orderId.ToString("N"));
    }

    // C: a notification delivered through the subscription re-queries authoritative state and re-renders.
    [Fact]
    public async Task A_pushed_notification_requeries_authoritative_state_and_rerenders()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Shipped));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        var queriesAfterRender = stubApiClient.CapturedQueries.Count;

        await subscription.DeliverAsync();

        stubApiClient.CapturedQueries.Count.Should().Be(queriesAfterRender + 1);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Shipped"));
    }

    // E1: cancel dispatched, the window elapses with no Cancelled push -> the page surfaces the degraded
    // ("taking longer") state from a timer it arms on dispatch.
    [Fact]
    public void No_settling_push_within_the_window_shows_the_degraded_state()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();

        fakeTime.Advance(TimeSpan.FromSeconds(31));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("taking longer than expected"));
    }

    // E2: a Cancelled push settles the button (it hides), driven by the subscription, not a clock advance.
    [Fact]
    public async Task A_cancelled_push_settles_and_hides_the_button()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Cancelled));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();

        await subscription.DeliverAsync();

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Cancel Order"));
    }

    // E3 (GREEN-on-write guard): once GREEN arms the degraded timer on dispatch, disposing the page must
    // cancel it. If it does not, advancing the clock fires MarkPollingTimeout on a disposed component and
    // throws. Green-on-write in RED (nothing arms a timer yet); it gains teeth with the GREEN timer.
    [Fact]
    public async Task Disposing_the_page_stops_the_degraded_timer_from_firing()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));
        stubApiClient.EnqueueCommandResult(typeof(CancelOrder), new CommandAcceptedResponse(BaseTime.UtcDateTime));

        var cut = Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));
        cut.Find("button").Click();
        await DisposeComponentsAsync();

        var advancePastWindow = () => fakeTime.Advance(TimeSpan.FromSeconds(31));

        advancePastWindow.Should().NotThrow();
    }

    // F: disposing the page deregisters by disposing the circuit subscription.
    [Fact]
    public async Task Disposing_the_page_disposes_the_subscription()
    {
        var orderId = Guid.NewGuid();
        stubApiClient.EnqueueQueryResult<GetOrderDetail, OrderDetailView?>(SampleDetail(orderId, OrderStatus.Placed));

        Render<OrderDetail>(p => p.Add(x => x.OrderId, orderId));

        await DisposeComponentsAsync();

        subscription.Disposed.Should().BeTrue();
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
