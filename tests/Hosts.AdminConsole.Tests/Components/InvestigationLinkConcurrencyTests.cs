using Bunit;
using EventSourcingCqrs.Hosts.AdminConsole.Browser;
using EventSourcingCqrs.Hosts.AdminConsole.Tracer;
using EventSourcingCqrs.Hosts.AdminConsole.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventSourcingCqrs.Hosts.AdminConsole.Tests.Components;

public class InvestigationLinkConcurrencyTests : BunitContext
{
    private const string First = "11111111111111111111111111111111";
    private const string Second = "22222222222222222222222222222222";

    [Fact]
    public async Task Stream_navigation_during_a_read_eventually_displays_the_latest_link()
    {
        Services.AddUnavailableCatalog();
        var reader = new DelayedInspector();
        Services.AddSingleton<IStreamInspector>(reader);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/streams?streamId=order:" + First);
        var cut = Render<EventStoreBrowser>();
        cut.WaitForAssertion(() => reader.Calls.Should().ContainSingle());
        await cut.InvokeAsync(() => nav.NavigateTo("/streams?streamId=order:" + Second));
        reader.Completion.SetResult(new(StreamInspectionOutcome.Empty, []));
        cut.WaitForAssertion(() => reader.Calls.Should().Equal("order:" + First, "order:" + Second));
        cut.WaitForAssertion(() => cut.Find("[data-detail-heading]").TextContent.Should().Contain(Second));
    }

    [Fact]
    public async Task Correlation_navigation_during_a_read_eventually_displays_the_latest_link()
    {
        Services.AddUnavailableCatalog();
        Services.AddSingleton(CorrelationTracerAvailability.Available);
        var reader = new DelayedTracer();
        Services.AddSingleton<ICorrelationTracer>(reader);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/correlations?correlationId=" + First);
        var cut = Render<CorrelationIdTracer>();
        cut.WaitForAssertion(() => reader.Calls.Should().ContainSingle());
        await cut.InvokeAsync(() => nav.NavigateTo("/correlations?correlationId=" + Second));
        reader.Completion.SetResult(new(CorrelationTraceOutcome.Empty, [], false));
        cut.WaitForAssertion(() => reader.Calls.Should().Equal(First, Second));
        cut.WaitForAssertion(() => cut.Find("[data-detail-heading]").TextContent.Should().Contain(Second));
    }

    [Fact]
    public async Task Replay_navigation_during_a_read_eventually_displays_the_latest_link()
    {
        var reader = new DelayedReplay();
        Services.AddSingleton<IOrderReplayLab>(reader);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/order-history?streamId=order:" + First);
        var cut = Render<OrderReplayLab>();
        cut.WaitForAssertion(() => reader.Calls.Should().ContainSingle());
        await cut.InvokeAsync(() => nav.NavigateTo("/order-history?streamId=order:" + Second));
        reader.Completion.SetResult(new(null, First));
        cut.WaitForAssertion(() => reader.Calls.Should().Equal("order:" + First, "order:" + Second));
        cut.WaitForAssertion(() => cut.Find("[role='status']").TextContent.Should().Contain(Second));
    }

    private sealed class DelayedInspector : IStreamInspector
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<StreamInspectionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StreamInspectionResult> InspectStreamAsync(string id, CancellationToken ct)
        {
            Calls.Add(id);
            return Calls.Count == 1 ? Completion.Task : Task.FromResult(new StreamInspectionResult(StreamInspectionOutcome.Empty, []));
        }
    }
    private sealed class DelayedTracer : ICorrelationTracer
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<CorrelationTraceView> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CorrelationTraceView> TraceCorrelationAsync(string id, CancellationToken ct)
        {
            Calls.Add(id);
            return Calls.Count == 1 ? Completion.Task : Task.FromResult(new CorrelationTraceView(CorrelationTraceOutcome.Empty, [], false));
        }
    }
    private sealed class DelayedReplay : IOrderReplayLab
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<OrderReplayResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OrderReplayResult> ReadAsync(string id, CancellationToken ct)
        {
            Calls.Add(id);
            return Calls.Count == 1 ? Completion.Task : Task.FromResult(new OrderReplayResult(null, id));
        }
    }
}
