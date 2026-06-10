using Bunit;
using EventSourcingCqrs.Hosts.Web.Components;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Components;

// The shared subscription-liveness badge (P11.9, ADR 0034). Presentational only: the page owns the
// LivenessState and the badge renders it. Idle renders nothing, mirroring the BadgeState.Idle convention,
// so a page that has not armed yet shows no liveness chrome. Each non-Idle state renders one stable
// distinguishing string under the #liveBadge id, the seam these specs and the page specs target.
public sealed class LiveBadgeTests : BunitContext
{
    [Fact]
    public void Idle_renders_nothing()
    {
        var cut = Render<LiveBadge>(p => p.Add(x => x.State, LivenessState.Idle));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Connecting_renders_the_connecting_pill()
    {
        var cut = Render<LiveBadge>(p => p.Add(x => x.State, LivenessState.Connecting));

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Connecting");
    }

    [Fact]
    public void Live_renders_the_live_pill()
    {
        var cut = Render<LiveBadge>(p => p.Add(x => x.State, LivenessState.Live));

        cut.Find("#liveBadge").TextContent.Trim().Should().Be("Live");
    }

    [Fact]
    public void Not_live_renders_the_default_detail()
    {
        var cut = Render<LiveBadge>(p => p.Add(x => x.State, LivenessState.NotLive));

        cut.Find("#liveBadge").TextContent.Should().Contain("Live updates unavailable; reload to refresh.");
    }

    [Fact]
    public void Not_live_renders_a_supplied_detail()
    {
        var cut = Render<LiveBadge>(p => p
            .Add(x => x.State, LivenessState.NotLive)
            .Add(x => x.NotLiveDetail, "Live updates are unavailable. The order may still be processing; check the orders list."));

        cut.Find("#liveBadge").TextContent.Should()
            .Contain("Live updates are unavailable. The order may still be processing; check the orders list.");
    }
}
