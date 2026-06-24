using Bunit;
using EventSourcingCqrs.Domain.Abstractions;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using EventSourcingCqrs.Hosts.AdminConsole.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

// The Replay Tool page's confirmation-gated rebuild action (Phase 12, commit 6b-2). The page depends on
// the narrow IOrderThroughputRebuild seam; these specs fake it (a one-method test double, no event store
// needed) and drive the page interactively over bUnit. Authorization is the host fallback gate (ADR
// 0040); the per-action RebuildProjection check is deferred (ADR 0041, Revisit when), so the action is
// rebuild-only and these specs carry no auth state.
public class ReplayToolActionTests : BunitContext
{
    private const string ValidTenant = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Clicking_rebuild_shows_a_confirmation_and_does_not_rebuild_before_confirm()
    {
        var rebuild = new RecordingRebuild();
        Services.AddSingleton<IOrderThroughputRebuild>(rebuild);

        var cut = Render<ReplayTool>();
        cut.Find("input").Change(ValidTenant);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("Confirm");
        rebuild.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Confirming_invokes_the_rebuild_with_the_parsed_tenant_and_renders_success()
    {
        var rebuild = new RecordingRebuild();
        Services.AddSingleton<IOrderThroughputRebuild>(rebuild);

        var cut = Render<ReplayTool>();
        cut.Find("input").Change(ValidTenant);
        cut.Find("button").Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm")).Click();

        rebuild.Calls.Should().ContainSingle()
            .Which.Should().Be(TenantId.From(Guid.Parse(ValidTenant)));
        cut.Markup.Should().Contain("Rebuild complete");
    }

    [Fact]
    public void A_failed_rebuild_renders_an_operator_visible_error()
    {
        Services.AddSingleton<IOrderThroughputRebuild>(new ThrowingRebuild());

        var cut = Render<ReplayTool>();
        cut.Find("input").Change(ValidTenant);
        cut.Find("button").Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Confirm")).Click();

        cut.Markup.Should().Contain("Rebuild failed");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void A_malformed_tenant_id_renders_a_validation_message_and_does_not_rebuild(string input)
    {
        var rebuild = new RecordingRebuild();
        Services.AddSingleton<IOrderThroughputRebuild>(rebuild);

        var cut = Render<ReplayTool>();
        cut.Find("input").Change(input);
        cut.Find("button").Click();

        cut.Markup.Should().Contain("valid tenant id");
        rebuild.Calls.Should().BeEmpty();
    }

    private sealed class RecordingRebuild : IOrderThroughputRebuild
    {
        public List<TenantId> Calls { get; } = [];

        public Task RebuildOrderThroughputAsync(TenantId tenant, CancellationToken ct)
        {
            Calls.Add(tenant);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRebuild : IOrderThroughputRebuild
    {
        public Task RebuildOrderThroughputAsync(TenantId tenant, CancellationToken ct)
            => throw new InvalidOperationException("rebuild failed");
    }
}
