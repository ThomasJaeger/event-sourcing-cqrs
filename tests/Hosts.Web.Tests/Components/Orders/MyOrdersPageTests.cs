using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Sales;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Domain.SharedKernel;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Authentication;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// The customer-facing order list under the owner-scoped CustomerOrders subscription
// (ADR 0037). The page arms one subscription keyed by the circuit's own customer id
// on first interactive render and re-queries the owner-scoped list on any
// notification; the server owner-scopes the query, so the page renders no customer
// column. Arm outcomes and liveness follow the landed LiveBadge vocabulary (ADR 0034).
public sealed class MyOrdersPageTests : BunitContext
{
    private static readonly DateTime PlacedAt = new(2026, 5, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly StubApiClient stubApiClient = new();
    private readonly StubCircuitResourceSubscription subscription = new();
    private readonly StubCircuitIdentityProvider identity = new();

    public MyOrdersPageTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<ICircuitResourceSubscription>(subscription);
        Services.AddSingleton<ICircuitForwardedIdentityProvider>(identity);
    }

    [Fact]
    public void Render_shows_the_loaded_orders_without_a_customer_column()
    {
        var cut = RenderPage(Row());
        cut.FindAll("tbody tr").Should().ContainSingle();
        cut.Markup.Should().NotContain("Customer");
    }

    [Fact]
    public void Page_arms_one_owner_scoped_subscription_keyed_by_the_circuits_customer_id()
    {
        RenderPage();
        subscription.StartCallCount.Should().Be(1);
        subscription.LastResourceType.Should().Be(SubscriptionResourceType.CustomerOrders);
        subscription.LastResourceId.Should().Be(identity.ActorId.ToString());
    }

    [Fact]
    public void A_successful_arm_at_render_surfaces_the_live_badge()
    {
        var cut = RenderPage();
        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    [Fact]
    public void A_cancellation_during_the_arm_is_caught_and_leaves_the_page_on_its_initial_data()
    {
        subscription.ThrowFromStart(new OperationCanceledException());
        var cut = RenderPage(Row());
        subscription.StartCallCount.Should().Be(1);
        subscription.Disposed.Should().BeFalse();
        cut.FindAll("tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void A_cancellation_during_the_arm_does_not_surface_the_not_live_badge()
    {
        subscription.ThrowFromStart(new OperationCanceledException());
        var cut = RenderPage(Row());
        cut.Markup.Should().NotContain("Live updates unavailable");
    }

    [Fact]
    public void An_arm_failure_at_render_surfaces_the_not_live_badge()
    {
        subscription.ThrowFromStart(new ApiInfrastructureException("subscription arm failed", statusCode: 503));
        var cut = RenderPage(Row());
        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable; reload to refresh.");
    }

    [Fact]
    public void An_unavailable_circuit_identity_surfaces_the_not_live_badge_and_never_arms()
    {
        identity.ThrowUnavailable();
        var cut = RenderPage(Row());
        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable; reload to refresh.");
        subscription.StartCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_pushed_notification_requeries_the_owner_scoped_list_and_rerenders()
    {
        var cut = RenderPage();
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(new[] { Row() });
        var queriesAfterRender = stubApiClient.CapturedQueries.Count;
        await subscription.DeliverAsync();
        stubApiClient.CapturedQueries.Count.Should().Be(queriesAfterRender + 1);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
    }

    [Fact]
    public async Task Disposing_the_page_disposes_the_subscription()
    {
        RenderPage();
        await DisposeComponentsAsync();
        subscription.DisposeCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    private IRenderedComponent<MyOrders> RenderPage(params OrderListRow[] rows)
    {
        stubApiClient.EnqueueQueryResult<ListOrders, IReadOnlyList<OrderListRow>>(rows);
        return Render<MyOrders>();
    }

    private static OrderListRow Row()
        => new(
            OrderId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            Status: OrderStatus.Placed,
            Total: new Money(149.95m, Currency.USD),
            PlacedUtc: PlacedAt,
            LastUpdatedUtc: PlacedAt,
            IsReturned: false,
            ReturnedUtc: null);
}
