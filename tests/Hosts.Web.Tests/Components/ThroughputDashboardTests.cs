using Bunit;
using EventSourcingCqrs.Application;
using EventSourcingCqrs.Application.Queries.Sales;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Domain.Sales.ReadModels;
using EventSourcingCqrs.Hosts.Web;
using EventSourcingCqrs.Hosts.Web.Components.Pages;
using EventSourcingCqrs.Hosts.Web.Hubs;
using EventSourcingCqrs.Hosts.Web.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// RED #7 (P11.12b): the admin order-throughput meter page (/admin/throughput). The page is still
// the shell from the compile-enabling turn, so these specs are expected to fail on absent rendered
// behavior and the un-wired subscription. The page mirrors the InventoryDashboard live-subscription
// shape (ADR 0032, ADR 0034): one collection-scoped subscription armed on first interactive render,
// a full re-query on each notification, and the LiveBadge as the only status surface. The meter issues
// no command, so it carries no operation/PendingBadge and no command-acceptance degraded timer.
public sealed class ThroughputDashboardTests : BunitContext
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly StubApiClient stubApiClient = new();
    private readonly FakeTimeProvider fakeTime = new(BaseTime);
    private readonly StubCircuitResourceSubscription subscription = new();

    public ThroughputDashboardTests()
    {
        Services.AddSingleton<IApiClient>(stubApiClient);
        Services.AddSingleton<TimeProvider>(fakeTime);
        Services.AddSingleton<ICircuitResourceSubscription>(subscription);
    }

    // B1
    [Fact]
    public void First_load_dispatches_the_throughput_query_and_renders_the_per_minute_rate()
    {
        var cut = RenderWith(Bucket(30, 7), Bucket(5, 5));

        stubApiClient.CapturedQueries.OfType<GetOrderThroughput>().Should().ContainSingle();
        cut.Markup.Should().Contain("12");
    }

    // B2
    [Fact]
    public void The_absolute_as_of_second_renders_beside_the_rate()
    {
        var cut = RenderWith(Bucket(30, 7), Bucket(5, 5));

        cut.Markup.Should().Contain("11:59:55");
    }

    // B3
    [Fact]
    public void The_age_renders_from_the_page_clock()
    {
        var cut = RenderWith(Bucket(30, 7), Bucket(5, 5));

        cut.Markup.Should().Contain("5 seconds");
    }

    // B4
    [Fact]
    public void On_first_render_the_subscription_arms_and_the_live_badge_renders()
    {
        var cut = RenderWith(Bucket(5, 5));

        subscription.StartCallCount.Should().Be(1);
        subscription.LastResourceType.Should().Be(SubscriptionResourceType.OrderThroughput);
        subscription.LastResourceId.Should().Be(CollectionResourceIds.OrderThroughputMetrics);
        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    // B5
    [Fact]
    public void An_arm_failure_surfaces_the_not_live_badge_and_keeps_the_subscription()
    {
        subscription.ThrowFromStart(new ApiInfrastructureException("arm failed", statusCode: 503));

        var cut = RenderWith(Bucket(5, 5));

        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable");
        subscription.Disposed.Should().BeFalse();
    }

    // B6
    [Fact]
    public void A_cancellation_during_the_arm_leaves_the_badge_connecting()
    {
        subscription.ThrowFromStart(new OperationCanceledException());

        var cut = RenderWith(Bucket(5, 5));

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Connecting");
    }

    // B7
    [Fact]
    public async Task A_push_requeries_the_throughput_and_rerenders_the_updated_rate()
    {
        var cut = RenderWith(Bucket(30, 7), Bucket(5, 5));
        stubApiClient.EnqueueQueryResult<GetOrderThroughput, IReadOnlyList<OrderThroughputRow>>(
            new[] { Bucket(5, 9) });
        var queriesAfterRender = stubApiClient.CapturedQueries.Count;

        await subscription.DeliverAsync();

        stubApiClient.CapturedQueries.Count.Should().Be(queriesAfterRender + 1);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("9"));
    }

    // B8
    [Fact]
    public void A_no_data_load_renders_the_empty_state()
    {
        var cut = RenderWith();

        cut.Markup.Should().Contain("no throughput data yet");
    }

    // B9
    [Fact]
    public void A_denied_load_renders_the_denied_state_and_never_arms_the_subscription()
    {
        stubApiClient.SeedQueryFailure<GetOrderThroughput>(
            new ApiAuthorizationException("forbidden", "FORBIDDEN"));

        var cut = Render<ThroughputDashboard>();

        cut.Markup.Should().Contain("not authorized");
        subscription.StartCallCount.Should().Be(0);
    }

    // B10
    [Fact]
    public async Task Disposing_the_page_disposes_the_subscription()
    {
        RenderWith(Bucket(5, 5));

        await DisposeComponentsAsync();

        subscription.Disposed.Should().BeTrue();
        subscription.DisposeCallCount.Should().Be(1);
    }

    // B11
    [Fact]
    public void The_meter_renders_only_the_live_badge_and_no_operation_badge()
    {
        var cut = RenderWith(Bucket(5, 5));

        cut.FindAll("#liveBadge").Should().ContainSingle();
        cut.Markup.Should().NotContain("bg-yellow-100");
    }

    private IRenderedComponent<ThroughputDashboard> RenderWith(params OrderThroughputRow[] buckets)
    {
        stubApiClient.EnqueueQueryResult<GetOrderThroughput, IReadOnlyList<OrderThroughputRow>>(buckets);
        return Render<ThroughputDashboard>();
    }

    private static OrderThroughputRow Bucket(int secondsBeforeBaseTime, long count)
        => new(BaseTime.UtcDateTime.AddSeconds(-secondsBeforeBaseTime), count);
}
