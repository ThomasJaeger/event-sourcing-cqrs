using EventSourcingCqrs.Hosts.Web.Hubs;
using FluentAssertions;
using Xunit;

namespace EventSourcingCqrs.Hosts.Web.Tests.Hubs;

public class DashboardHubTests
{
    [Fact]
    public async Task SubscribeToResource_adds_the_connection_to_the_group()
    {
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub
        {
            Context = new FakeHubCallerContext("conn-1"),
            Groups = groups,
        };

        await hub.SubscribeToResource("order:order-7");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("add", "conn-1", "order:order-7"));
    }

    [Fact]
    public async Task UnsubscribeFromResource_removes_the_connection_from_the_group()
    {
        var groups = new RecordingGroupManager();
        using var hub = new DashboardHub
        {
            Context = new FakeHubCallerContext("conn-1"),
            Groups = groups,
        };

        await hub.UnsubscribeFromResource("order:order-7");

        groups.Calls.Should().ContainSingle()
            .Which.Should().Be(("remove", "conn-1", "order:order-7"));
    }
}
