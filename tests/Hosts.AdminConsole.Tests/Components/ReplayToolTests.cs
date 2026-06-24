using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// The Replay Tool page (/replay) lets an operator rebuild the order-throughput read model for one
// tenant (Phase 12, ADR 0041). This render spec is the route-and-controls half (commit 6b-1): it proves
// the page renders the operator controls behind the host fallback gate (ADR 0040, upstream of render, so
// the page carries no denied branch). The rebuild action and its confirmation gate are commit 6b-2, so
// no click-triggers-rebuild behavior is asserted here.
public class ReplayToolTests : BunitContext
{
    [Fact]
    public void The_replay_tool_page_renders_the_tenant_input_and_rebuild_control()
    {
        Services.AddSingleton<IOrderThroughputRebuild>(new NoopRebuild());

        var cut = Render<ReplayTool>();

        // The operator enters the target tenant id.
        cut.FindAll("input").Should().NotBeEmpty();
        // The page names what it rebuilds: the order-throughput read model.
        cut.Markup.Should().Contain("order-throughput");
        // The rebuild control the confirmation-gated action triggers.
        cut.Find("button").TextContent.Should().Contain("Rebuild");
    }

    private sealed class NoopRebuild : IOrderThroughputRebuild
    {
        public Task RebuildOrderThroughputAsync(TenantId tenant, CancellationToken ct) => Task.CompletedTask;
    }
}
